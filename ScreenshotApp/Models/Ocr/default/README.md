# 截影默认 OCR 模型

- 检测：PP-OCRv5 mobile 文本检测 ONNX 模型
- 识别：PP-OCRv5 mobile 中文识别 ONNX 模型，支持中文、英文、数字和常用符号
- 推理：ONNX Runtime CPU
- 方向分类：第一版默认关闭，接口保留

模型文件在开发阶段统一固化，并通过 `manifest.json` 记录 SHA-256。程序不会在用户电脑转换模型，也不会上传截图或记录 OCR 完整文本。

模型来源：PaddleOCR / RapidOCR 模型清单，Apache-2.0 许可。识别模型必须与 `dict.txt` 配套替换。
