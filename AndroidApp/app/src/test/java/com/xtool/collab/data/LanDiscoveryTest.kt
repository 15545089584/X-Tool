package com.xtool.collab.data

import java.net.InetAddress
import org.junit.Assert.*
import org.junit.Test

class LanDiscoveryTest {
    private fun ip(value: String) = InetAddress.getByName(value)

    @Test fun hotspotBroadcastAndPeer() {
        val local = ip("10.176.123.168")
        val broadcast = LanDiscovery.broadcast(local, 24)!!
        assertEquals("10.176.123.255", broadcast.hostAddress)
        val binding = LanDiscovery.Binding(local, broadcast, 24)
        assertTrue(LanDiscovery.sameSubnet(binding, ip("10.176.123.130")))
        assertFalse(LanDiscovery.sameSubnet(binding, ip("192.168.31.213")))
        assertFalse(LanDiscovery.sameSubnet(binding, local))
    }

    @Test fun directedBroadcastSupportsDifferentMasks() {
        assertEquals("192.168.43.255", LanDiscovery.broadcast(ip("192.168.43.1"), 24)?.hostAddress)
        assertEquals("172.20.10.15", LanDiscovery.broadcast(ip("172.20.10.1"), 28)?.hostAddress)
        assertEquals("10.176.127.255", LanDiscovery.broadcast(ip("10.176.123.168"), 20)?.hostAddress)
    }

    @Test fun noBroadcastToPublicVpnOrPointToPointAddresses() {
        for (host in listOf("100.75.51.13", "8.8.8.8", "127.0.0.1", "169.254.1.1", "::1"))
            assertNull(LanDiscovery.broadcast(ip(host), 24))
        assertNull(LanDiscovery.broadcast(ip("10.1.1.1"), 0))
        assertNull(LanDiscovery.broadcast(ip("10.1.1.1"), 31))
        assertNull(LanDiscovery.broadcast(ip("10.1.1.1"), 32))
    }

    @Test fun hotspotInterfacesAreNotMistakenForVpnOrCellular() {
        for (name in listOf("wlan0", "ap0", "swlan0", "wlan1", "br0", "eth0"))
            assertFalse(LanDiscovery.excludedInterface(name))
        for (name in listOf("tun0", "rmnet_data0", "v4-rmnet_data0", "ccmni0", "tailscale0", "wg0"))
            assertTrue(LanDiscovery.excludedInterface(name))
    }

    @Test fun useObservedPeerInsteadOfAdvertisedStaleAddress() {
        assertEquals("10.176.123.130:18120", LanDiscovery.responseHost(ip("10.176.123.130"), "192.168.31.213:18120"))
        assertNull(LanDiscovery.responseHost(ip("10.176.123.130"), "192.168.31.213:99999"))
        assertNull(LanDiscovery.responseHost(ip("10.176.123.130"), "invalid"))
        assertNull(LanDiscovery.responseHost(ip("8.8.8.8"), "192.168.31.213:18120"))
    }
}
