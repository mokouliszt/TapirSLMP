#!/usr/bin/env python3
"""Raw 3E/4E write-read smoke test through a TapirSLMP sealer."""

from __future__ import annotations

import argparse
import socket
import struct


def request(frame: bytes, host: str, port: int) -> bytes:
    with socket.create_connection((host, port), timeout=15) as connection:
        connection.sendall(frame)
        prefix = receive_exact(connection, 2)
        if prefix == b"\xd0\x00":
            header = prefix + receive_exact(connection, 7)
            length = struct.unpack_from("<H", header, 7)[0]
        elif prefix == b"\xd4\x00":
            header = prefix + receive_exact(connection, 11)
            length = struct.unpack_from("<H", header, 11)[0]
        else:
            raise AssertionError(f"unexpected response subheader: {prefix.hex()}")
        return header + receive_exact(connection, length)


def receive_exact(connection: socket.socket, length: int) -> bytes:
    chunks = bytearray()
    while len(chunks) < length:
        chunk = connection.recv(length - len(chunks))
        if not chunk:
            raise EOFError("peer closed before a complete frame was received")
        chunks.extend(chunk)
    return bytes(chunks)


def build_frame(frame4e: bool, serial: int, command: int, data: bytes) -> bytes:
    subcommand = 0x0002  # iQ-R extended device specification
    body = struct.pack("<HHH", 0x0010, command, subcommand) + data
    route = struct.pack("<BBHBH", 0, 0xFF, 0x03FF, 0, len(body))
    if frame4e:
        return b"\x54\x00" + struct.pack("<HH", serial, 0) + route + body
    return b"\x50\x00" + route + body


def device_d(head: int) -> bytes:
    return struct.pack("<IH", head, 0x00A8)


def assert_success(response: bytes, frame4e: bool, expected_serial: int) -> bytes:
    if frame4e:
        assert response[:2] == b"\xd4\x00"
        assert struct.unpack_from("<H", response, 2)[0] == expected_serial
        end_code_offset = 13
    else:
        assert response[:2] == b"\xd0\x00"
        end_code_offset = 9
    end_code = struct.unpack_from("<H", response, end_code_offset)[0]
    assert end_code == 0, f"SLMP end code: 0x{end_code:04X}"
    return response[end_code_offset + 2 :]


def roundtrip(host: str, port: int, frame4e: bool, head: int, value: int, serial: int) -> None:
    write_data = device_d(head) + struct.pack("<HH", 1, value)
    write_response = request(build_frame(frame4e, serial, 0x1401, write_data), host, port)
    assert_success(write_response, frame4e, serial)

    read_data = device_d(head) + struct.pack("<H", 1)
    read_serial = (serial + 1) & 0xFFFF
    read_response = request(build_frame(frame4e, read_serial, 0x0401, read_data), host, port)
    payload = assert_success(read_response, frame4e, read_serial)
    assert payload == struct.pack("<H", value), (
        f"D{head} expected 0x{value:04X}, got {payload.hex()}"
    )


def main() -> None:
    parser = argparse.ArgumentParser()
    parser.add_argument("--host", default="127.0.0.1")
    parser.add_argument("--port", type=int, default=5000)
    args = parser.parse_args()

    roundtrip(args.host, args.port, False, 100, 0x1234, 0)
    roundtrip(args.host, args.port, True, 101, 0x5678, 0x2345)
    print("TapirSLMP raw 3E/4E round-trips passed")


if __name__ == "__main__":
    main()
