package com.clipshare.core.network

import org.junit.Assert.assertEquals
import org.junit.Assert.assertTrue
import org.junit.Test

class EndpointPolicyTest {
    @Test
    fun releaseRequiresHttps() {
        assertTrue(
            EndpointPolicy.validate("http://example.test/", BuildChannel.RELEASE) is EndpointValidation.Rejected,
        )
        assertTrue(
            EndpointPolicy.validate("https://example.test/", BuildChannel.RELEASE) is EndpointValidation.Accepted,
        )
    }

    @Test
    fun debugAndTestRejectExternalHosts() {
        assertTrue(
            EndpointPolicy.validate(
                "https://production.example/",
                BuildChannel.DEBUG_OR_TEST,
            ) is EndpointValidation.Rejected,
        )
        val accepted = EndpointPolicy.validate("http://10.0.2.2:8000", BuildChannel.DEBUG_OR_TEST)
        assertTrue(accepted is EndpointValidation.Accepted)
        assertEquals("/", (accepted as EndpointValidation.Accepted).baseUrl.encodedPath)
    }

    @Test
    fun rejectsMalformedCredentialsQueryAndFragment() {
        assertTrue(EndpointPolicy.validate("not a url", BuildChannel.RELEASE) is EndpointValidation.Rejected)
        assertTrue(
            EndpointPolicy.validate("https://user:secret@example.test/", BuildChannel.RELEASE) is
                EndpointValidation.Rejected,
        )
        assertTrue(
            EndpointPolicy.validate("https://:secret@example.test/", BuildChannel.RELEASE) is
                EndpointValidation.Rejected,
        )
        assertTrue(
            EndpointPolicy.validate("https://example.test/?q=1", BuildChannel.RELEASE) is
                EndpointValidation.Rejected,
        )
        assertTrue(
            EndpointPolicy.validate("https://example.test/#fragment", BuildChannel.RELEASE) is
                EndpointValidation.Rejected,
        )
    }

    @Test
    fun debugAcceptsAllFrozenLocalHostsAndNormalizesPath() {
        for (host in listOf("localhost", "127.0.0.1", "10.0.2.2")) {
            val result = EndpointPolicy.validate("http://$host:8000/api", BuildChannel.DEBUG_OR_TEST)
            assertTrue(result is EndpointValidation.Accepted)
            assertEquals("/api/", (result as EndpointValidation.Accepted).baseUrl.encodedPath)
        }
    }
}
