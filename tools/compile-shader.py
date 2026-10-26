"""Compiles a WPF pixel shader (ps_2_0) with the d3dcompiler_47.dll that ships with Windows.

Usage: python -I tools/compile-shader.py <source.fx> <output.ps>
The output is committed next to the source and embedded as a WPF resource; the app never compiles shaders at run time.
"""
import ctypes
import sys
from ctypes import wintypes

source_path, output_path = sys.argv[1], sys.argv[2]
source = open(source_path, "rb").read()

d3d = ctypes.WinDLL("d3dcompiler_47.dll")


class Blob(ctypes.Structure):
    pass


BlobPtr = ctypes.POINTER(Blob)
# ID3DBlob vtable: QueryInterface, AddRef, Release, GetBufferPointer, GetBufferSize
VTable = ctypes.POINTER(ctypes.c_void_p * 5)
Blob._fields_ = [("vtable", VTable)]

d3d.D3DCompile.argtypes = [ctypes.c_void_p, ctypes.c_size_t, ctypes.c_char_p, ctypes.c_void_p, ctypes.c_void_p,
                           ctypes.c_char_p, ctypes.c_char_p, wintypes.UINT, wintypes.UINT,
                           ctypes.POINTER(BlobPtr), ctypes.POINTER(BlobPtr)]
d3d.D3DCompile.restype = ctypes.c_long


def blob_bytes(blob):
    vt = blob.contents.vtable.contents
    pointer = ctypes.WINFUNCTYPE(ctypes.c_void_p, BlobPtr)(vt[3])(blob)
    size = ctypes.WINFUNCTYPE(ctypes.c_size_t, BlobPtr)(vt[4])(blob)
    return ctypes.string_at(pointer, size)


code, errors = BlobPtr(), BlobPtr()
D3DCOMPILE_OPTIMIZATION_LEVEL3 = 1 << 15
hr = d3d.D3DCompile(source, len(source), source_path.encode(), None, None, b"main", b"ps_2_0",
                    D3DCOMPILE_OPTIMIZATION_LEVEL3, 0, ctypes.byref(code), ctypes.byref(errors))
if hr != 0:
    message = blob_bytes(errors).decode(errors="replace") if errors else ""
    raise SystemExit(f"D3DCompile failed 0x{hr & 0xffffffff:08x}: {message}")
open(output_path, "wb").write(blob_bytes(code))
print(f"{output_path}: {len(blob_bytes(code))} bytes")
