"""Create and verify the Windows portable and source archives."""
from hashlib import sha256
from pathlib import Path
from zipfile import ZipFile, ZIP_DEFLATED

root = Path(__file__).resolve().parents[1]
dist = root / 'dist'
portable = root / '.work' / 'portable'

def bundle(filename, base, members, required):
    target = dist / filename
    with ZipFile(target, 'w', ZIP_DEFLATED, compresslevel=6) as archive:
        for path in sorted(members):
            archive.write(path, f'VgmDesk/{path.relative_to(base).as_posix()}')
    with ZipFile(target) as archive:
        names = set(archive.namelist())
        assert all(f'VgmDesk/{name}' in names for name in required), names
        assert archive.testzip() is None
    print(f'{sha256(target.read_bytes()).hexdigest()}  {target.name} ({target.stat().st_size:,} bytes)')

bundle('VgmDesk-1.0.0-win-x64.zip', portable, (p for p in portable.rglob('*') if p.is_file()),
       ['VgmDesk.exe', 'vgmstream/vgmstream-cli.exe', 'vgmstream/COPYING', 'README.md', 'licenses/wpf-LICENSE.txt'])
source = [root / 'README.md', root / 'package.sh']
for folder in ('src', 'tests'):
    source.extend(p for p in (root / folder).iterdir() if p.is_file())
bundle('VgmDesk-1.0.0-source.zip', root, source, ['src/VgmDesk.csproj', 'tests/EngineSmoke.csproj', 'README.md'])
