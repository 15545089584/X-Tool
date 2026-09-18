package com.xtool.collab.notification

import org.junit.Assert.*
import org.junit.Test

class ListenerRetryGateTest {
    @Test fun frequentChecksDoNotFloodRebind() {
        val gate = ListenerRetryGate()
        assertTrue(gate.allow(0)); assertFalse(gate.allow(1)); assertFalse(gate.allow(4999))
        assertTrue(gate.allow(5000)); assertFalse(gate.allow(14999)); assertTrue(gate.allow(15000))
    }
    @Test fun restoredListenerResetsBackoff() {
        val gate = ListenerRetryGate(); gate.allow(0); gate.allow(5000)
        gate.reset(); assertTrue(gate.allow(6000)); assertFalse(gate.allow(10999)); assertTrue(gate.allow(11000))
    }
    @Test fun userCanRetryDuringBackoff() {
        val gate = ListenerRetryGate(); gate.allow(0)
        assertTrue(gate.allow(1, manual = true)); assertFalse(gate.allow(2))
    }
}
