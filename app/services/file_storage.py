"""文件存储服务：磁盘读写的唯一入口，流式红线落地点。

红线：任何文件内容一律分块读写（64KB），禁止无参 read() 全量读入内存；
save_streamed 流中累计计数，超限立即中断并清理半成品文件，
保证「413 响应后磁盘无残留」。
"""
import secrets
from pathlib import Path
from typing import BinaryIO

from app.core.errors import ShareFileTooLargeError

# 流式读写分块大小：64KB；超过阈值立即中断而非读到文件尾
CHUNK_SIZE = 64 * 1024


class FileStorage:
    """本地磁盘文件存储。

    stored_name 由 secrets.token_hex(16) 生成（32 字符、无扩展名），
    用户原始文件名仅存元数据，绝不参与磁盘路径——路径穿越在源头被掐断。
    """

    def __init__(self, base_dir: Path) -> None:
        self.base_dir = base_dir

    def path(self, stored_name: str) -> Path:
        """解析存储路径：resolve 后必须仍位于 base_dir 内，防路径穿越。

        正常调用 stored_name 均由系统生成，但删除等路径来自 DB 记录——
        任何历史脏数据都不能越出存储目录（resolve + is_relative_to 双防御）。
        非法路径抛 ValueError，调用方视作内部错误处理。
        """
        candidate = (self.base_dir / stored_name).resolve()
        base = self.base_dir.resolve()
        if not candidate.is_relative_to(base):
            raise ValueError(f"非法存储路径: {stored_name}")
        return candidate

    def save_streamed(self, source: BinaryIO, *, max_size: int) -> str:
        """流式落盘：64KB 逐块读源并写目标，累计超过 max_size 立即中断。

        返回生成的 stored_name（无扩展名）。流程：
        1. 每块先累计计数再落盘——超限的块不写、不继续读，立即抛
           ShareFileTooLargeError（413），保证源未被读穿、磁盘无超限数据；
        2. 任何异常路径（含超限）都 unlink 半成品文件，磁盘保持干净。
        """
        stored_name = secrets.token_hex(16)
        target = self.path(stored_name)
        target.parent.mkdir(parents=True, exist_ok=True)
        written = 0
        try:
            with target.open("wb") as out:
                while True:
                    chunk = source.read(CHUNK_SIZE)
                    if not chunk:
                        break
                    written += len(chunk)
                    if written > max_size:
                        raise ShareFileTooLargeError(
                            f"文件超过大小上限（{max_size} 字节）"
                        )
                    out.write(chunk)
            return stored_name
        except BaseException:
            # 超限 / IO 错误 / 中断：半成品一律清理，磁盘不允许残留
            target.unlink(missing_ok=True)
            raise

    def read_preview(self, stored_name: str, *, max_size: int) -> bytes:
        """截断读：最多读取 max_size 字节用于预览，不加载全文件。

        预览只需文件头部（如 200KB），全量读入内存违背流式红线。
        文件不存在（含已被懒删）抛 FileNotFoundError，由调用方映射 404。
        """
        with self.path(stored_name).open("rb") as f:
            return f.read(max_size)

    def delete(self, stored_name: str) -> None:
        """删除存储文件：幂等，文件不存在不报错。

        懒删路径可能被多次触发（并发访问过期文件），幂等保证安全；
        stored_name 经 path() 防穿越校验后才参与文件系统操作。
        """
        self.path(stored_name).unlink(missing_ok=True)
