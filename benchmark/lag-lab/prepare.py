import gzip
import hashlib
import io
import pathlib
import shutil
import tarfile
import tempfile
import urllib.request

root = pathlib.Path(__file__).resolve().parent
pin = '5c168be5ef6a32ac966daf5983502a4f694b37ad'
archive_path = root / 'turso.tar.gz'
blob = archive_path.read_bytes() if archive_path.exists() else urllib.request.urlopen(f'https://codeload.github.com/tursodatabase/turso/tar.gz/{pin}').read()
if hashlib.sha256(blob).hexdigest() != '7ea83d5e28577cf9d8655b77705902379e06b82fb29424f6ea328b5f26f17ecb':
    raise ValueError('Turso source archive digest mismatch')
archive_path.write_bytes(blob)
with tempfile.TemporaryDirectory(prefix='verified-source-', dir=root) as staging:
    staging = pathlib.Path(staging)
    with tarfile.open(fileobj=io.BytesIO(blob)) as archive:
        archive.extractall(staging, filter='data')
    source = staging / f'turso-{pin}'
    manifest = source / 'Cargo.toml'
    manifest.write_text(manifest.read_text().replace('members = [', 'members = [\n    "sync-host-shim",', 1))
    shim = source / 'sync-host-shim'
    shim.mkdir()
    (shim / 'Cargo.toml').write_bytes((root / 'shim.toml').read_bytes())
    (shim / 'lib.rs').write_text((source / 'cli/sync_server.rs').read_text() + (root / 'bridge.rs').read_text())
    (source / 'Cargo.lock').write_bytes(gzip.decompress((root / 'Cargo.lock.gz').read_bytes()))
    previous = root / 'turso'
    if previous.exists():
        previous.rename(staging / 'previous')
        target = staging / 'previous/target'
        if target.exists():
            shutil.move(target, source / 'target')
    source.rename(previous)
