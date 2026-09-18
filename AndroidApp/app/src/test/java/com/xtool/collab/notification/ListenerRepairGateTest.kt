package com.xtool.collab.notification

import org.junit.Assert.*
import org.junit.Test

class ListenerRepairGateTest {
    @Test fun normalRebindGetsGracePeriod() {
        val gate = ListenerRepairGate()
        assertFalse(gate.shouldRepair(0)); assertFalse(gate.shouldRepair(14999))
        assertTrue(gate.shouldRepair(15000)); assertFalse(gate.shouldRepair(15001))
    }
    @Test fun reconnectionDoesNotBypassRateLimit() {
        val gate = ListenerRepairGate()
        gate.shouldRepair(0); assertTrue(gate.shouldRepair(15000))
        gate.connected(); assertFalse(gate.shouldRepair(20000))
        assertFalse(gate.shouldRepair(35000)); assertTrue(gate.shouldRepair(615000))
    }
    @Test fun healthyConnectionClearsMissingDuration() {
        val gate = ListenerRepairGate()
        gate.shouldRepair(0); gate.connected()
        assertFalse(gate.shouldRepair(20000)); assertTrue(gate.shouldRepair(35000))
    }
}
