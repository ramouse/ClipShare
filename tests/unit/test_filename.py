"""文件名净化与扩展名解析单元测试（纯函数，无需数据库）。"""
from app.domain.filename import MAX_FILENAME_BYTES, sanitize_filename, split_extensions


def test_unix_path_keeps_basename() -> None:
    """目录穿越型路径：../../etc/passwd → passwd。"""
    assert sanitize_filename("../../etc/passwd") == "passwd"


def test_windows_path_keeps_basename() -> None:
    """Windows 盘符路径：C:\\x\\y.exe → y.exe。"""
    assert sanitize_filename("C:\\x\\y.exe") == "y.exe"


def test_empty_name_falls_back_to_file() -> None:
    """空名兜底：空串与全空白均返回 file。"""
    assert sanitize_filename("") == "file"
    assert sanitize_filename("   ") == "file"
    assert sanitize_filename("..") == "file"


def test_name_truncated_to_255_bytes() -> None:
    """超长截断：ASCII 恰好 255 字符；多字节按 UTF-8 字节截断且不切半。"""
    assert sanitize_filename("a" * 300) == "a" * MAX_FILENAME_BYTES
    assert len(sanitize_filename("a" * 300)) == MAX_FILENAME_BYTES
    # 多字节字符：截断后字节数仍不超上限，且可正常解码（errors="ignore" 不抛）
    long_name = "长" * 300
    result = sanitize_filename(long_name)
    assert len(result.encode("utf-8")) <= MAX_FILENAME_BYTES


def test_control_chars_replaced() -> None:
    """控制字符与 Windows 保留字符替换为下划线。"""
    assert sanitize_filename("a\x00b\x1fc.txt") == "a_b_c.txt"
    assert sanitize_filename("a*b|c?d") == "a_b_c_d"


def test_split_extensions_normalizes() -> None:
    """逗号分隔白名单：小写、去空白、前导点归一。"""
    assert split_extensions("txt, .MD, png ") == {"txt", "md", "png"}
    assert split_extensions("..md") == {"md"}
    assert split_extensions("") == set()
    assert split_extensions(" ,, ") == set()
