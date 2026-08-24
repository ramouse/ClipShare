"""无需密钥的 ENC1 envelope 严格解析，用于服务器上传边界校验。"""
import base64
import binascii
import re
from io import BytesIO
from typing import BinaryIO

ENC1_PREFIX = "ENC1:"
ENC1_IV_BYTES = 12
ENC1_TAG_BYTES = 16
ENC1_MAX_MARKER_BYTES = 16 * 1024 * 1024
_BASE64URL = re.compile(r"^[A-Za-z0-9_-]+$")
_BASE64URL_BYTES = frozenset(b"ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789-_")
_BASE64URL_INDEX = {
    value: index
    for index, value in enumerate(
        b"ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789-_"
    )
}


class Enc1EnvelopeError(ValueError):
    """ENC1 线格式不合法。"""


def _decode_base64url(value: str) -> bytes:
    if not _BASE64URL.fullmatch(value) or len(value) % 4 == 1:
        raise Enc1EnvelopeError("Base64URL 格式非法")
    padded = value + "=" * ((4 - len(value) % 4) % 4)
    try:
        decoded = base64.b64decode(padded, altchars=b"-_", validate=True)
    except (binascii.Error, ValueError) as exc:
        raise Enc1EnvelopeError("Base64URL 解码失败") from exc
    canonical = base64.urlsafe_b64encode(decoded).rstrip(b"=").decode("ascii")
    if canonical != value:
        raise Enc1EnvelopeError("Base64URL 不是规范编码")
    return decoded


def _decoded_length(encoded_length: int, last_byte: int) -> int:
    remainder = encoded_length % 4
    if encoded_length == 0 or remainder == 1:
        raise Enc1EnvelopeError("Base64URL 长度非法")
    # 无 padding Base64URL 的最后未使用位必须为 0，否则存在非规范别名。
    index = _BASE64URL_INDEX[last_byte]
    if remainder == 2 and index & 0x0F:
        raise Enc1EnvelopeError("Base64URL pad bits 非零")
    if remainder == 3 and index & 0x03:
        raise Enc1EnvelopeError("Base64URL pad bits 非零")
    return (encoded_length * 6) // 8


def encrypted_plaintext_size_stream(source: BinaryIO) -> int:
    """流式验证 ENC1 marker，并由编码长度推导明文字节数。"""
    prefix = ENC1_PREFIX.encode("ascii")
    prefix_offset = 0
    iv_chars = bytearray()
    cipher_length = 0
    cipher_last = -1
    in_cipher = False
    marker_size = 0
    while True:
        chunk = source.read(64 * 1024)
        if not chunk:
            break
        marker_size += len(chunk)
        if marker_size > ENC1_MAX_MARKER_BYTES:
            raise Enc1EnvelopeError("ENC1 marker 超过 16 MiB 解析上限")
        for value in chunk:
            if prefix_offset < len(prefix):
                if value != prefix[prefix_offset]:
                    raise Enc1EnvelopeError("缺少 ENC1 前缀")
                prefix_offset += 1
                continue
            if not in_cipher:
                if value == ord("."):
                    if not iv_chars:
                        raise Enc1EnvelopeError("ENC1 IV 为空")
                    in_cipher = True
                    continue
                if value not in _BASE64URL_BYTES:
                    raise Enc1EnvelopeError("ENC1 IV 含非法字符")
                iv_chars.append(value)
                if len(iv_chars) > 16:
                    raise Enc1EnvelopeError("ENC1 IV 编码过长")
                continue
            if value not in _BASE64URL_BYTES:
                raise Enc1EnvelopeError("ENC1 密文含非法字符或多余分隔符")
            cipher_length += 1
            cipher_last = value

    if prefix_offset != len(prefix) or not in_cipher or cipher_length == 0:
        raise Enc1EnvelopeError("ENC1 marker 不完整")
    iv = _decode_base64url(iv_chars.decode("ascii"))
    if len(iv) != ENC1_IV_BYTES:
        raise Enc1EnvelopeError("ENC1 IV 必须为 12 字节")
    combined_length = _decoded_length(cipher_length, cipher_last)
    if combined_length < ENC1_TAG_BYTES:
        raise Enc1EnvelopeError("ENC1 密文必须包含 16 字节认证标签")
    return combined_length - ENC1_TAG_BYTES


def encrypted_plaintext_size(marker_bytes: bytes) -> int:
    """bytes 便捷入口；生产文件路径使用流式版本。"""
    return encrypted_plaintext_size_stream(BytesIO(marker_bytes))
