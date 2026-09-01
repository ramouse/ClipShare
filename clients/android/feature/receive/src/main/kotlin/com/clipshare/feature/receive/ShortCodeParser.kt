package com.clipshare.feature.receive

import java.net.URI

sealed interface CodeParseResult {
    data class Accepted(val code: String) : CodeParseResult
    data class Rejected(val message: String) : CodeParseResult
}

object ShortCodeParser {
    private val codePattern = Regex("^[A-Za-z0-9]{1,8}$")

    fun parse(raw: String): CodeParseResult {
        val value = raw.trim()
        return if (codePattern.matches(value)) {
            CodeParseResult.Accepted(value)
        } else {
            parseLink(value)
        }
    }

    private fun parseLink(value: String): CodeParseResult {
        val uri = try {
            URI(value)
        } catch (ignored: java.net.URISyntaxException) {
            return CodeParseResult.Rejected("请输入 1–8 位 Base62 短码或 HTTP(S) 链接")
        }
        return when {
            uri.scheme !in setOf("http", "https") || uri.host.isNullOrBlank() ->
                CodeParseResult.Rejected("请输入 1–8 位 Base62 短码或 HTTP(S) 链接")
            !uri.fragment.isNullOrBlank() ->
                CodeParseResult.Rejected("A1 不处理链接片段中的端到端密文；该能力属于 C2")
            else -> parsePathCode(uri)
        }
    }

    private fun parsePathCode(uri: URI): CodeParseResult {
        val code = uri.path.trimEnd('/').substringAfterLast('/')
        return if (codePattern.matches(code)) {
            CodeParseResult.Accepted(code)
        } else {
            CodeParseResult.Rejected("链接中没有有效的 1–8 位 Base62 短码")
        }
    }
}
