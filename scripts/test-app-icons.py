#!/usr/bin/env python3
"""Check the icon group and PNG resources in the published PE without shell scaling.

Formats: https://learn.microsoft.com/windows/win32/debug/pe-format
https://learn.microsoft.com/windows/win32/menurc/resource-file-formats
"""
import argparse
import runpy
import struct
from pathlib import Path

SIZES = (16, 20, 24, 32, 40, 48, 64, 128, 256)


def resources(data):
    if data[:2] != b'MZ':
        raise ValueError('Expected a Windows executable')
    pe = struct.unpack_from('<I', data, 60)[0]
    if data[pe:pe + 4] != b'PE\0\0':
        raise ValueError('Invalid PE signature')
    count = struct.unpack_from('<H', data, pe + 6)[0]
    optional_size = struct.unpack_from('<H', data, pe + 20)[0]
    optional = pe + 24
    magic = struct.unpack_from('<H', data, optional)[0]
    if magic not in (0x10b, 0x20b) or count > 96:
        raise ValueError('Unsupported PE header')
    directories = optional + (112 if magic == 0x20b else 96)
    root_rva = struct.unpack_from('<I', data, directories + 16)[0]
    sections = [struct.unpack_from('<IIII', data, optional + optional_size + index * 40 + 8) for index in range(count)]

    def position(rva):
        for virtual_size, address, raw_size, raw in sections:
            if address <= rva < address + min(virtual_size, raw_size):
                return raw + rva - address
        raise ValueError('Resource address is outside the PE sections')

    root = position(root_rva)
    result = {}

    def walk(offset, path):
        if len(path) > 3:
            raise ValueError('Unexpected resource hierarchy')
        named, ids = struct.unpack_from('<HH', data, root + offset + 12)
        if named + ids > 4096:
            raise ValueError('Unexpected resource count')
        for index in range(named + ids):
            name, child = struct.unpack_from('<II', data, root + offset + 16 + index * 8)
            if name & 0x80000000:
                name_position = root + (name & 0x7fffffff)
                length = struct.unpack_from('<H', data, name_position)[0]
                name = data[name_position + 2:name_position + 2 + length * 2].decode('utf-16-le')
            next_path = (*path, name)
            if child & 0x80000000:
                walk(child & 0x7fffffff, next_path)
            else:
                rva, size = struct.unpack_from('<II', data, root + child)
                start = position(rva)
                payload = data[start:start + size]
                if len(payload) != size:
                    raise ValueError('Truncated resource')
                result[next_path] = payload
    walk(0, ())
    return result


def verify(executable, ico):
    data = executable.read_bytes()
    entries = resources(data)
    groups = [(path, payload) for path, payload in entries.items() if path[0] == 14]
    if len(groups) != 1:
        raise ValueError(f'Expected one primary icon group, found {len(groups)}')
    group_path, group = groups[0]
    if struct.unpack_from('<HHH', group) != (0, 1, len(SIZES)):
        raise ValueError('Executable icon group does not contain all nine sizes')
    for index, size in enumerate(SIZES):
        expected = struct.unpack_from('<BBBBHHII', ico, 6 + index * 16)
        actual = struct.unpack_from('<BBBBHHIH', group, 6 + index * 14)
        if expected[:7] != actual[:7]:
            raise ValueError(f'Executable icon directory differs at {size}px')
        key = (3, actual[7], group_path[2])
        if key not in entries:
            raise ValueError(f'Executable icon resource missing at {size}px')
        payload = ico[expected[7]:expected[7] + expected[6]]
        if entries[key] != payload:
            raise ValueError(f'Executable icon pixels differ at {size}px')
    print(f'PASS embedded executable icon: {executable} ({len(SIZES)} exact PNG resource frames)')


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('executables', nargs='+', type=Path)
    args = parser.parse_args()
    root = Path(__file__).resolve().parents[1]
    ico = (root / 'src/windows/assets/optibackup.ico').read_bytes()
    validator = runpy.run_path(str(root / 'scripts/generate-icons.py'))
    validator['canonical']('optibackup.ico', ico)
    for executable in args.executables:
        try:
            verify(executable, ico)
        except (ValueError, struct.error, OSError, IndexError) as error:
            raise SystemExit(f'Executable icon validation failed: {executable}: {error}') from error


if __name__ == '__main__':
    main()
