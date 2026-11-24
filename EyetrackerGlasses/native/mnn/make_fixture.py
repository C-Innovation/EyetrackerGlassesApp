#!/usr/bin/env python3
"""Generate two independent FP32 outputs with known values."""
from pathlib import Path
import sys
import onnx
from onnx import TensorProto, helper

destination = Path(sys.argv[1])
destination.parent.mkdir(parents=True, exist_ok=True)
shape = [1, 3, 2, 2]  # Three channels exercise packed NC4HW4 -> ordinary NCHW copies.
nodes = [helper.make_node("Mul", ["images", "scale"], ["scaled"]),
         helper.make_node("Add", ["scaled", "bias"], ["affine"]),
         helper.make_node("Relu", ["images"], ["relu"])]
graph = helper.make_graph(nodes, "MnnBridgeCheck",
    [helper.make_tensor_value_info("images", TensorProto.FLOAT, shape)],
    [helper.make_tensor_value_info("affine", TensorProto.FLOAT, shape),
     helper.make_tensor_value_info("relu", TensorProto.FLOAT, shape)],
    [helper.make_tensor("scale", TensorProto.FLOAT, [1], [0.5]),
     helper.make_tensor("bias", TensorProto.FLOAT, [1], [0.25])])
model = helper.make_model(graph, opset_imports=[helper.make_opsetid("", 13)], ir_version=8)
onnx.checker.check_model(model)
onnx.save(model, destination)
print(destination)
