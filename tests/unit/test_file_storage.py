"""FileStorage 单元测试：流式落盘、超限中断、防穿越、截断读、幂等删除。

全程使用 tmp_path，不触碰真实存储目录；超限用例通过带读取计数器的
源断言「未读穿」与「磁盘无残留」两条红线。
"""
import io
from pathlib import Path

import pytest

from app.core.errors import ShareFileTooLargeError
from app.services.file_storage import CHUNK_SIZE, FileStorage


class _CountingSource:
    """带读取字节计数器的流式源：用于断言超限中断时未读穿整个源。"""

    def __init__(self, data: bytes) -> None:
        self._data = data
        self._pos = 0
        self.bytes_read = 0

    def read(self, size: int = -1) -> bytes:
        end = len(self._data) if size < 0 else min(self._pos + size, len(self._data))
        chunk = self._data[self._pos : end]
        self._pos = end
        self.bytes_read += len(chunk)
        return chunk


def test_save_streamed_roundtrip_bytes_identical(tmp_path: Path) -> None:
    """字节级一致：落盘后按 stored_name 读回，与源内容完全一致（含二进制）。"""
    content = b"\x00\xff" + b"x" * 1000 + b"\x80\x81" + b"y" * 500
    storage = FileStorage(tmp_path)
    stored = storage.save_streamed(io.BytesIO(content), max_size=10 * 1024 * 1024)
    # stored_name 为 32 位十六进制、无扩展名（磁盘不暴露原始文件名）
    assert stored == stored.lower() and len(stored) == 32
    assert (tmp_path / stored).read_bytes() == content


def test_save_streamed_aborts_before_reading_all(tmp_path: Path) -> None:
    """超限流中断言：抛 413 异常、源未被读穿、目标目录无任何残留。"""
    storage = FileStorage(tmp_path)
    # 源为两块 + 10 字节：第二块读入即超限，第三块绝不应被读取
    source = _CountingSource(b"z" * (CHUNK_SIZE * 2 + 10))
    with pytest.raises(ShareFileTooLargeError):
        storage.save_streamed(source, max_size=CHUNK_SIZE)
    # 未读穿：读取量 = 2 块（触发超限时恰读了两块），远小于全量
    assert source.bytes_read == CHUNK_SIZE * 2
    assert source.bytes_read < CHUNK_SIZE * 2 + 10
    # 无残留：半成品文件已被 unlink，目录为空
    assert list(tmp_path.iterdir()) == []


def test_path_rejects_traversal(tmp_path: Path) -> None:
    """path() 防穿越：../ 与绝对路径（resolve + is_relative_to）一律拒绝。"""
    storage = FileStorage(tmp_path)
    with pytest.raises(ValueError):
        storage.path("../evil.txt")
    with pytest.raises(ValueError):
        storage.path("/etc/passwd")
    with pytest.raises(ValueError):
        storage.path("sub/../../evil.txt")


def test_read_preview_truncates(tmp_path: Path) -> None:
    """read_preview 截断读：超过 max_size 只返回头部；未超限返回全部。"""
    storage = FileStorage(tmp_path)
    stored = storage.save_streamed(io.BytesIO(b"a" * 1000), max_size=1024 * 1024)
    assert storage.read_preview(stored, max_size=10) == b"a" * 10
    assert storage.read_preview(stored, max_size=5000) == b"a" * 1000


def test_delete_is_idempotent(tmp_path: Path) -> None:
    """delete 幂等：首次删除成功，重复删除与删除不存在文件均不抛错。"""
    storage = FileStorage(tmp_path)
    stored = storage.save_streamed(io.BytesIO(b"data"), max_size=1024)
    storage.delete(stored)
    assert not (tmp_path / stored).exists()
    storage.delete(stored)  # 第二次删除：懒删路径重复触发安全
