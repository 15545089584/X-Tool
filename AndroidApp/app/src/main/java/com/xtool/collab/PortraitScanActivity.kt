package com.xtool.collab

import android.app.Activity
import android.content.Intent
import android.content.pm.ActivityInfo
import android.os.Bundle
import android.view.View
import com.journeyapps.barcodescanner.DecoratedBarcodeView

/**
 * 竖屏扫码页：蓝色激光线在取景框内从上到下扫描。
 * 结果通过 SCAN_RESULT 返回，与 ScanContract 兼容。
 */
class PortraitScanActivity : Activity() {
    private lateinit var scannerView: DecoratedBarcodeView

    override fun onCreate(savedInstanceState: Bundle?) {
        requestedOrientation = ActivityInfo.SCREEN_ORIENTATION_PORTRAIT
        super.onCreate(savedInstanceState)
        setContentView(R.layout.scan_layout)

        scannerView = findViewById(R.id.zxing_barcode_scanner)
        scannerView.initializeFromIntent(intent)
        scannerView.decodeSingle { result ->
            setResult(RESULT_OK, Intent().putExtra("SCAN_RESULT", result?.text ?: ""))
            finish()
        }
        findViewById<View>(R.id.close_button).setOnClickListener { finish() }
    }

    override fun onResume() {
        super.onResume()
        scannerView.resume()
    }

    override fun onPause() {
        scannerView.pause()
        super.onPause()
    }
}
