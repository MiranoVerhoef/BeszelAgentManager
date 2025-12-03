# -*- mode: python ; coding: utf-8 -*-


a = Analysis(
    ['beszel_agent_manager\\main.py'],
    pathex=[],
    binaries=[],
    datas=[('BeszelAgentManager_icon.ico', '.'), ('BeszelAgentManager_icon_512.png', '.')],
    hiddenimports=[],
    hookspath=[],
    hooksconfig={},
    runtime_hooks=[],
    excludes=[],
    noarchive=False,
    optimize=0,
)
pyz = PYZ(a.pure)

exe = EXE(
    pyz,
    a.scripts,
    a.binaries,
    a.datas,
    [],
    name='BeszelAgentManager2.0.0',
    debug=False,
    bootloader_ignore_signals=False,
    strip=False,
    upx=True,
    upx_exclude=[],
    runtime_tmpdir=None,
    console=False,
    disable_windowed_traceback=False,
    argv_emulation=False,
    target_arch=None,
    codesign_identity=None,
    entitlements_file=None,
    icon=['BeszelAgentManager_icon.ico'],
)
