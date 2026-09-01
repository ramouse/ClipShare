package com.clipshare.core.model

import java.nio.charset.StandardCharsets

object TextPolicy {
    fun validate(raw: String): TextValidation {
        val rejection = when {
            raw.isBlank() -> TextValidation.Rejected("内容不能为空")
            raw.length > MAX_TEXT_CONTRACT_CHARS ->
                TextValidation.Rejected("文本超过服务端 100000 字符限制")
            raw.toByteArray(StandardCharsets.UTF_8).size > MAX_TEXT_UTF8_BYTES ->
                TextValidation.Rejected("文本超过 100 KiB UTF-8 限制")
            else -> null
        }
        return rejection ?: TextValidation.Accepted(raw)
    }
}
