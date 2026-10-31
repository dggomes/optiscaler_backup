"""Exercise the real catalog producer's archive and append-only policies offline."""
import hashlib
import importlib.util
import pathlib
import tempfile
import unittest
import zipfile

ROOT = pathlib.Path(__file__).resolve().parents[1]
spec = importlib.util.spec_from_file_location('component_catalog', ROOT / 'scripts/update-component-catalog.py')
catalog = importlib.util.module_from_spec(spec)
spec.loader.exec_module(catalog)


class CatalogPolicies(unittest.TestCase):
    def test_archive_fingerprints_and_traversal(self):
        with tempfile.TemporaryDirectory() as temporary:
            path = pathlib.Path(temporary) / 'source.zip'
            with zipfile.ZipFile(path, 'w') as archive:
                archive.writestr('wrapper/OptiScaler.dll', b'known binary bytes')
                archive.writestr('setup.bat', b'must never execute')
            rows = list(catalog.binary_hashes(path, 'unused'))
            self.assertEqual(rows, [('wrapper/OptiScaler.dll', hashlib.sha256(b'known binary bytes').hexdigest(), 18)])
            with zipfile.ZipFile(path, 'w') as archive:
                archive.writestr('../OptiScaler.dll', b'escape')
            with self.assertRaises(ValueError):
                list(catalog.binary_hashes(path, 'unused'))

    def test_zip_case_collision_and_link(self):
        with tempfile.TemporaryDirectory() as temporary:
            path = pathlib.Path(temporary) / 'source.zip'
            with zipfile.ZipFile(path, 'w') as archive:
                archive.writestr('OptiScaler.dll', b'a')
                archive.writestr('OPTISCALER.DLL', b'b')
            with self.assertRaises(ValueError):
                list(catalog.binary_hashes(path, 'unused'))
            with zipfile.ZipFile(path, 'w') as archive:
                link = zipfile.ZipInfo('OptiScaler.dll')
                link.external_attr = 0o120777 << 16
                archive.writestr(link, b'outside')
            with self.assertRaises(ValueError):
                list(catalog.binary_hashes(path, 'unused'))

    def test_observations_preserve_replaced_assets(self):
        old = {'Component': 'OptiScaler', 'Sha256': 'a' * 64, 'SourceRevision': 'first-source'}
        same = dict(old, SourceRevision='later-source')
        replaced = dict(old, Sha256='b' * 64)
        self.assertEqual(catalog.identity(old), catalog.identity(same))
        self.assertNotEqual(catalog.identity(old), catalog.identity(replaced))


if __name__ == '__main__':
    unittest.main()
