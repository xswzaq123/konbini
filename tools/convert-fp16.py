"""Build-time only: makes a half-precision copy of an ONNX model (inputs/outputs stay float32, so callers don't change).
Usage: python convert-fp16.py <in.onnx> <out.onnx>   (needs onnx + onnxruntime in a throwaway venv)"""
import sys, onnx
from onnxruntime.transformers.float16 import convert_float_to_float16
m = onnx.load(sys.argv[1])
# Resize stays float32: Windows ML rejects half-precision resize scales.
m16 = convert_float_to_float16(m, keep_io_types=True, op_block_list=['Resize'])
onnx.save(m16, sys.argv[2])
import onnxruntime as ort   # validates types the same way Windows ML does
ort.InferenceSession(sys.argv[2], providers=['CPUExecutionProvider'])
print('saved and validated', sys.argv[2])
