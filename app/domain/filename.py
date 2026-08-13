"""文件名净化与扩展名解析纯函数：不依赖 IO，供服务层与配置层复用。

安全红线：用户文件名绝不直接用于磁盘路径（磁盘名由 secrets 生成），
此处只负责把「展示用的原始文件名」净化成安全、可存储的元数据值。
"""
import re

# Windows 保留字符（路径分隔符、非法字符与控制字符统一替换为下划线）
_INVALID_CHARS = re.compile(r'[\\/:*?"<>|\x00-\x1f]')
# 文件名最大长度（UTF-8 字节）：与 NTFS 255 上限及 original_name 列宽（255）对齐
MAX_FILENAME_BYTES = 255


def sanitize_filename(name: str) -> str:
    """净化用户提供的文件名：去路径成分、替换非法字符、截断、空名兜底。

    仅保留最后一段路径分量（Linux/Windows 分隔符均处理），把非法与控制
    字符替换为下划线，超长按 UTF-8 字节截断（不把多字节字符切半），
    最终结果为空时兜底为 "file"。
    """
    # 统一分隔符后取最后一段：../../etc/passwd → passwd、C:\\x\\y.exe → y.exe
    name = re.sub(r"[\\/]+", "/", name).rsplit("/", 1)[-1]
    # 去除首尾空白与结尾点：Windows 会静默剥离文件名末尾的点/空格，提前归一避免混淆
    name = name.strip().rstrip(".")
    name = _INVALID_CHARS.sub("_", name)
    if not name:
        return "file"
    encoded = name.encode("utf-8")
    if len(encoded) > MAX_FILENAME_BYTES:
        # 按字节截断并以 errors="ignore" 解码，丢弃被切半的多字节字符尾部
        name = encoded[:MAX_FILENAME_BYTES].decode("utf-8", errors="ignore")
    return name or "file"


def split_extensions(raw: str) -> set[str]:
    """解析逗号分隔的扩展名白名单：小写、去空白、前导点归一。

    "txt, .MD, png " → {"txt", "md", "png"}；空串 / 全空白 → 空集合。
    """
    extensions: set[str] = set()
    for part in raw.split(","):
        # 前导点/尾点全剥（"..md"→"md"、"md."→"md"），配置笔误不再静默失配
        ext = part.strip().lower().lstrip(".").rstrip(".").strip()
        if ext:
            extensions.add(ext)
    return extensions
