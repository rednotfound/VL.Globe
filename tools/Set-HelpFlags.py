"""Usage: python tools/Set-HelpFlags.py help/VL.Globe

Adds F1 HelpFocus flags to VL.Globe's help patches: one High per node across the library,
Low where a node also appears as a supporting part. Idempotent: an existing flag is replaced."""
import re, sys, os

help_dir = sys.argv[1]
H, L = 'High', 'Low'
plan = {
    r'Overview\HowTo Open a globe window.vl': {'GlobeWindow': H, 'OpenStreetMap': H, 'GlobeCamera': L, 'GlobeViewInfo': L},
    r'Overview\HowTo Build your own globe scene.vl': {'Globe': H, 'GlobeCamera': H, 'Drag': H, 'ZoomByWheel': H, 'DragRotate': H, 'OpenStreetMap': L, 'GlobeViewInfo': L},
    r'Layers\HowTo Stack tile layers.vl': {'XyzTiles': H, 'Group': H, 'Group (Spectral)': H, 'OpenStreetMap': L, 'GlobeWindow': L},
    r'Layers\HowTo Draw vector tiles.vl': {'VectorTiles': H, 'MapLibreStyle (URL)': H, 'MapLibreStyle (File)': H},
    r'Layers\HowTo Choose a vector tile style.vl': {'VectorTiles': L, 'MapLibreStyle (URL)': L, 'Geometry': L, 'VectorStyle': L, 'Group': L},
    r'Layers\HowTo Draw shapes on the globe.vl': {'Geometry': H, 'VectorStyle': H, 'FeatureLayer': H, 'Group': L},
    r'Layers\HowTo Draw a graticule.vl': {'Graticule': H},
    r'Lights\HowTo Light the buildings with the sun.vl': {'SunLight': H, 'VectorTiles': L},
    r'Camera\HowTo Fly to a place.vl': {'FlyTo': H, 'CenterOn': H, 'ZoomToLevel': H, 'ZoomIn': H, 'ZoomOut': H, 'ZoomToBounds': H},
    r'Input\HowTo Read the place under the cursor.vl': {'ScreenToGlobe': H, 'GlobeViewInfo': H, 'GlobeToScreen': H},
    r'Input\HowTo Pick a feature.vl': {'Pick': H, 'Geometry': L},
    r'Objects\HowTo Place objects on the globe.vl': {'GlobePositionComponent': H, 'ToGlobe': H, 'FromGlobe': H, 'ToGlobe (Coordinate)': H, 'GlobeViewInfo': L},
}

# a node of VL.Globe: its NodeReference names the node by ProcessAppFlag/OperationCallFlag and comes from VL.Globe
node_re = re.compile(
    r'(<p:NodeReference LastCategoryFullName="Globe[^"]*" LastDependency="VL\.Globe\.(?:vl|dll)">'
    r'(?:(?!</p:NodeReference>).)*?<Choice Kind="(?:ProcessAppFlag|OperationCallFlag)" Name="([^"]+)" />'
    r'(?:(?!</p:NodeReference>).)*?</p:NodeReference>)'
    r'(\s*<p:HelpFocus[^>]*>(?:High|Low)</p:HelpFocus>)?', re.S)

highs = {}
for rel, flags in plan.items():
    path = os.path.join(help_dir, rel)
    raw = open(path, 'rb').read()
    bom = raw.startswith(b'\xef\xbb\xbf')
    text = raw.decode('utf-8-sig')
    nl = '\r\n' if '\r\n' in text else '\n'
    seen = set()

    def flag(m):
        name = m.group(2)
        level = flags.get(name)
        if level is None or name in seen:
            return m.group(1)               # not planned here, or a second instance: no flag
        seen.add(name)
        # the indentation of </p:NodeReference>
        indent = re.search(r'([ \t]*)</p:NodeReference>$', m.group(1)).group(1)
        return m.group(1) + nl + indent + f'<p:HelpFocus p:Assembly="VL.Lang" p:Type="VL.Model.HelpPriority">{level}</p:HelpFocus>'

    new = node_re.sub(flag, text)
    missing = set(flags) - seen
    if missing:
        sys.exit(f'{rel}: no node {sorted(missing)}')
    for n, lv in flags.items():
        if lv == H:
            if n in highs: sys.exit(f'{n}: High in {highs[n]} and {rel}')
            highs[n] = rel
    open(path, 'wb').write((b'\xef\xbb\xbf' if bom else b'') + new.encode('utf-8'))
    print(f'{rel}: ' + ', '.join(f'{n} {lv}' for n, lv in flags.items()))
print(f'{len(highs)} nodes with a High flag')
