package com.xtool.collab.data

import java.net.Inet4Address
import java.net.InetAddress
import java.net.NetworkInterface

/** 按实际局域网接口发现电脑，支持手机作为热点提供方，不依赖 Wi-Fi 客户端状态。 */
internal object LanDiscovery {
    data class Binding(val address: InetAddress, val broadcast: InetAddress, val prefixLength: Int)

    fun isPrivateIpv4(address: InetAddress): Boolean {
        if (address !is Inet4Address) return false
        val a = address.address.map { it.toInt() and 255 }
        return a[0] == 10 || (a[0] == 172 && a[1] in 16..31) || (a[0] == 192 && a[1] == 168)
    }

    fun broadcast(address: InetAddress, prefixLength: Int): InetAddress? {
        if (!isPrivateIpv4(address) || prefixLength !in 1..30) return null
        val value = address.address.fold(0L) { result, byte -> (result shl 8) or (byte.toLong() and 255) }
        val mask = (0xffffffffL shl (32 - prefixLength)) and 0xffffffffL
        val target = (value and mask) or (mask xor 0xffffffffL)
        return InetAddress.getByAddress(ByteArray(4) { index -> (target shr (24 - index * 8)).toByte() })
    }

    fun sameSubnet(binding: Binding, peer: InetAddress): Boolean =
        isPrivateIpv4(peer) && broadcast(peer, binding.prefixLength) == binding.broadcast && peer != binding.address

    fun bindings(): List<Binding> = runCatching {
        NetworkInterface.getNetworkInterfaces()?.toList().orEmpty()
            .filter { adapter ->
                adapter.isUp && !adapter.isLoopback && !adapter.isPointToPoint &&
                    !excludedInterface(adapter.name)
            }
            .flatMap { adapter ->
                adapter.interfaceAddresses.mapNotNull { address ->
                    val prefix = address.networkPrefixLength.toInt()
                    val target = broadcast(address.address, prefix) ?: return@mapNotNull null
                    Binding(address.address, target, prefix)
                }
            }.distinctBy { it.address }.take(8)
    }.getOrDefault(emptyList())

    internal fun excludedInterface(name: String): Boolean =
        Regex("^(tun|tap|ppp|rmnet|v4-rmnet|ccmni|pdp|wwan|tailscale|wg)", RegexOption.IGNORE_CASE).containsMatchIn(name)

    /** 使用响应的实际来源地址；不能被电脑错误公布的另一块网卡地址带偏。 */
    fun responseHost(peer: InetAddress, advertisedHost: String): String? {
        if (!isPrivateIpv4(peer)) return null
        val port = advertisedHost.substringAfterLast(':', "").toIntOrNull() ?: return null
        if (port !in 1..65535) return null
        return "${peer.hostAddress}:$port"
    }
}
