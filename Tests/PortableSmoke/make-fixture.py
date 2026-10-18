"""Create an original Identity ONNX fixture without external Python dependencies."""
from pathlib import Path
import sys

def varint(n):
    data = bytearray()
    while n > 127:
        data.append((n & 127) | 128)
        n >>= 7
    return bytes(data) + bytes([n])

def integer(key, n):
    return varint(key * 8) + varint(n)

def field(key, value):
    if isinstance(value, str):
        value = value.encode()
    return varint(key * 8 + 2) + varint(len(value)) + value

def value(name):
    shape = field(1, integer(1, 1)) + field(1, integer(1, 4))
    return field(1, name) + field(2, field(1, integer(1, 1) + field(2, shape)))

graph = field(1, field(1, 'input') + field(2, 'output') + field(4, 'Identity'))
graph += field(2, 'AmuseIdentity') + field(11, value('input')) + field(12, value('output'))
Path(sys.argv[1]).write_bytes(integer(1, 8) + field(7, graph) + field(8, integer(2, 18)))
