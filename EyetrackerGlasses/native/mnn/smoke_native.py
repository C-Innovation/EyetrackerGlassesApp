#!/usr/bin/env python3
"""Run the C ABI against the make_fixture.py model; no Python MNN or ONNX Runtime needed."""
import ctypes as c
from pathlib import Path
import sys

library, model_path = map(Path, sys.argv[1:3])
lib = c.CDLL(str(library.resolve()))
lib.mnn_bridge_abi_version.restype = c.c_int
assert lib.mnn_bridge_abi_version() == 1
lib.mnn_create.argtypes = [c.c_void_p, c.c_size_t, c.c_int, c.c_int, c.c_void_p, c.c_size_t]
lib.mnn_create.restype = c.c_void_p
lib.mnn_destroy.argtypes = [c.c_void_p]
lib.mnn_input_info.argtypes = [c.c_void_p, c.POINTER(c.c_int), c.c_int, c.POINTER(c.c_int), c.POINTER(c.c_int)]
lib.mnn_run.argtypes = [c.c_void_p, c.POINTER(c.c_float), c.c_int, c.c_void_p, c.c_size_t]
lib.mnn_output_count.argtypes = [c.c_void_p]
lib.mnn_output_info.argtypes = [c.c_void_p, c.c_int, c.POINTER(c.c_int), c.c_int,
                               c.POINTER(c.c_int), c.POINTER(c.c_int), c.c_void_p, c.c_size_t]
lib.mnn_output_copy.argtypes = [c.c_void_p, c.c_int, c.POINTER(c.c_float), c.c_int]
data = c.create_string_buffer(model_path.read_bytes())
error = c.create_string_buffer(4096)
ctx = lib.mnn_create(data, len(data) - 1, 2, 0, error, len(error))
assert ctx, error.value.decode()
try:
    dims = (c.c_int * 8)()
    count, nhwc = c.c_int(), c.c_int()
    rank = lib.mnn_input_info(ctx, dims, 8, c.byref(count), c.byref(nhwc))
    assert rank == 4 and list(dims)[:rank] == [1, 3, 2, 2] and count.value == 12 and nhwc.value == 0
    values = (c.c_float * 12)(*[i - 6 for i in range(12)])
    assert lib.mnn_run(ctx, values, 11, error, len(error)) == -1
    assert lib.mnn_output_count(ctx) == 0
    assert lib.mnn_run(ctx, values, 12, error, len(error)) == 0, error.value.decode()
    assert lib.mnn_output_count(ctx) == 2
    for index in range(2):
        name = c.create_string_buffer(4096)
        rank = lib.mnn_output_info(ctx, index, dims, 8, c.byref(count), c.byref(nhwc), name, len(name))
        assert rank == 4 and count.value == 12
        output = (c.c_float * 12)()
        assert lib.mnn_output_copy(ctx, index, output, 11) == -1
        assert lib.mnn_output_copy(ctx, index, output, 12) == 0
        expected = [(i - 6) * .5 + .25 for i in range(12)]
        if name.value == b"relu":
            expected = [max(0, i - 6) for i in range(12)]
        else:
            assert name.value == b"affine", name.value
        assert all(abs(a - b) < 1e-5 for a, b in zip(output, expected)), (name.value, list(output), expected)
        print("PASS:", name.value.decode(), "FP32 shape/layout/numeric output")
    assert lib.mnn_output_copy(ctx, -1, (c.c_float * 12)(), 12) == -1
finally:
    lib.mnn_destroy(ctx)
print("PASS: native ABI, buffer lengths and inference")
