"""ClipShare 命令行工具包（M5 创新点 B）。

零第三方新依赖：仅标准库 argparse + 主依赖 httpx。
"""

from cli.main import get_share, main, send_share

__all__ = ["main", "send_share", "get_share"]
