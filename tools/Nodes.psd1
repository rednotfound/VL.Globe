# Every node VL.Globe contributes: the name a patch uses -> the C# a resolved call must contain
# (a static method compiles to `Class.Method(`, a process node to `new ClassNode(`).
# Read by Compile-HelpPatches.ps1 (the node table of rung 2) and Test-VLPatch.ps1 (the F1 audit),
# so a node added to the package is added here once. 31 nodes on 2026-11-24.
@{
    Nodes = @{
        # Globe
        'ToGlobe'                = 'GlobeCoordinates\.ToGlobe\('
        'ToGlobe (Coordinate)'   = 'GlobeCoordinates\.ToGlobeCoordinate\('
        'FromGlobe'              = 'GlobeCoordinates\.FromGlobe\('
        'Globe'                  = 'new\s+[\w\.]*GlobeNode\('
        'GlobeWindow'            = 'GlobeWindow_P\.Create\('     # patched in VL.Globe.vl, not C#
        'GlobeCamera'            = 'new\s+[\w\.]*GlobeCameraNode\('
        'Drag'                   = 'new\s+[\w\.]*GlobeDragNode\('
        'ZoomByWheel'            = 'new\s+[\w\.]*GlobeZoomByWheelNode\('
        'DragRotate'             = 'new\s+[\w\.]*GlobeDragRotateNode\('
        'ScreenToGlobe'          = 'GlobeViewNodes\.ScreenToGlobe\('
        'GlobeToScreen'          = 'GlobeViewNodes\.GlobeToScreen\('
        'GlobeViewInfo'          = 'GlobeViewNodes\.GlobeViewInfo\('
        'GlobePositionComponent' = 'new\s+[\w\.]*GlobePositionComponentNode\('
        'Pick'                   = 'new\s+[\w\.]*GlobePickNode\('
        # Globe.Layers
        'OpenStreetMap'          = 'new\s+[\w\.]*OpenStreetMapNode\('
        'XyzTiles'               = 'new\s+[\w\.]*XyzTilesNode\('
        'Geometry'               = 'new\s+[\w\.]*GlobeGeometryLayerNode\('
        'FeatureLayer'           = 'new\s+[\w\.]*GlobeFeatureLayerNode\('
        'VectorTiles'            = 'new\s+[\w\.]*GlobeVectorTilesNode\('
        'Group'                  = 'new\s+[\w\.]*GlobeGroupNode\('
        'Group (Spectral)'       = 'new\s+[\w\.]*GlobeGroupSpectralNode\('
        'Graticule'              = 'new\s+[\w\.]*GlobeGraticuleNode\('
        # Globe.Styles
        'VectorStyle'            = 'GlobeStyleNodes\.VectorStyle\('
        'MapLibreStyle (URL)'    = 'new\s+[\w\.]*MapLibreStyleUrlNode\('
        'MapLibreStyle (File)'   = 'new\s+[\w\.]*MapLibreStyleFileNode\('
        # Globe.Navigate
        'CenterOn'               = 'new\s+[\w\.]*GlobeCenterOnNode\('
        'ZoomToLevel'            = 'new\s+[\w\.]*GlobeZoomToLevelNode\('
        'ZoomIn'                 = 'new\s+[\w\.]*GlobeZoomInNode\('
        'ZoomOut'                = 'new\s+[\w\.]*GlobeZoomOutNode\('
        'FlyTo'                  = 'new\s+[\w\.]*GlobeFlyToNode\('
        'ZoomToBounds'           = 'new\s+[\w\.]*GlobeZoomToBoundsNode\('
    }
}
