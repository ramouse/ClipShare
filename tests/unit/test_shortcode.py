"""Base62 短码生成单元测试。"""
from app.domain.shortcode import ALPHABET, generate_shortcode


def test_default_length_is_six() -> None:
    """默认长度为 6。"""
    assert len(generate_shortcode()) == 6


def test_custom_length() -> None:
    """显式指定长度时按指定长度生成。"""
    assert len(generate_shortcode(8)) == 8
    assert len(generate_shortcode(1)) == 1


def test_all_chars_in_alphabet() -> None:
    """生成结果每个字符都落在 Base62 字符表内。"""
    for _ in range(50):
        assert all(char in ALPHABET for char in generate_shortcode())


def test_no_duplicates_among_1000() -> None:
    """生成 1000 个短码无重复（62^6 空间下碰撞概率可忽略）。"""
    codes = {generate_shortcode() for _ in range(1000)}
    assert len(codes) == 1000
