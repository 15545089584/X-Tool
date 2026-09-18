package com.xtool.collab.notification

import org.junit.Assert.*
import org.junit.Test
import java.net.URI
import java.util.Base64

class NotificationPairingTest {
    private val payload = """{"kind":"xtool-notifications-v1","host":"192.168.8.104","port":18122,"serverId":"pc","fingerprint":"${"A".repeat(64)}","secret":"${"B".repeat(64)}"}"""
    private fun qr(body: String = payload) = "http://192.168.8.104:18120/pair?pin=123456#xtool-notify=" + Base64.getUrlEncoder().withoutPadding().encodeToString(body.toByteArray())
    @Test fun primaryQrCarriesIdentityWithoutHttpQueryLeak() {
        assertEquals("192.168.8.104", NotificationPairing.parse(qr(), "pc").host)
        assertEquals("pin=123456", URI(qr()).query)
    }
    @Test fun preservesExplicitOffAndDefaultsFirstBindingOn() {
        assertTrue(NotificationPairing.enabledAfterBinding(null))
        assertFalse(NotificationPairing.enabledAfterBinding(false))
        assertTrue(NotificationPairing.enabledAfterBinding(true))
    }
    @Test fun acceptsLegacyNotificationQr() { assertEquals("A".repeat(64), NotificationPairing.parse(payload, "pc").fingerprint) }
    @Test fun rejectsOtherComputer() { assertThrows(IllegalArgumentException::class.java) { NotificationPairing.parse(qr(), "other") } }
    @Test fun rejectsLegacyHttpQrWithoutIdentity() { assertThrows(IllegalArgumentException::class.java) { NotificationPairing.parse("http://192.168.8.104:18120/pair?pin=123456", "pc") } }
    @Test fun rejectsInvalidCertificate() { assertThrows(IllegalArgumentException::class.java) { NotificationPairing.parse(qr(payload.replace("A".repeat(64), "invalid")), "pc") } }
    @Test fun rejectsInvalidAddress() { assertThrows(IllegalArgumentException::class.java) { NotificationPairing.parse(qr(payload.replace("192.168.8.104", "999.168.8.104")), "pc") } }
}
