# Third-party notices

VL.Globe is MIT licensed (LICENSE). It contains or derives from the work below; the packages it depends on
(NetTopologySuite, NetTopologySuite.Features, BruTile, VL.NetTopologySuite) are installed by NuGet under
their own licences and are not part of this package.

## MapLibre GL JS

`shaders\GlobeVector.sdsl`, `GlobeSymbol.sdsl`, `GlobeCircle.sdsl` and the vector-tile engine in
`src\VL.Globe\Engine` (buckets, line tessellation, symbol shaping and placement, style expressions) are
written after MapLibre GL JS - https://github.com/maplibre/maplibre-gl-js - and checked against its render
tests. Its licence:

```
Copyright (c) 2023, MapLibre contributors

All rights reserved.

Redistribution and use in source and binary forms, with or without modification,
are permitted provided that the following conditions are met:

    * Redistributions of source code must retain the above copyright notice,
      this list of conditions and the following disclaimer.
    * Redistributions in binary form must reproduce the above copyright notice,
      this list of conditions and the following disclaimer in the documentation
      and/or other materials provided with the distribution.
    * Neither the name of MapLibre GL JS nor the names of its contributors
      may be used to endorse or promote products derived from this software
      without specific prior written permission.

THIS SOFTWARE IS PROVIDED BY THE COPYRIGHT HOLDERS AND CONTRIBUTORS
"AS IS" AND ANY EXPRESS OR IMPLIED WARRANTIES, INCLUDING, BUT NOT
LIMITED TO, THE IMPLIED WARRANTIES OF MERCHANTABILITY AND FITNESS FOR
A PARTICULAR PURPOSE ARE DISCLAIMED. IN NO EVENT SHALL THE COPYRIGHT OWNER OR
CONTRIBUTORS BE LIABLE FOR ANY DIRECT, INDIRECT, INCIDENTAL, SPECIAL,
EXEMPLARY, OR CONSEQUENTIAL DAMAGES (INCLUDING, BUT NOT LIMITED TO,
PROCUREMENT OF SUBSTITUTE GOODS OR SERVICES; LOSS OF USE, DATA, OR
PROFITS; OR BUSINESS INTERRUPTION) HOWEVER CAUSED AND ON ANY THEORY OF
LIABILITY, WHETHER IN CONTRACT, STRICT LIABILITY, OR TORT (INCLUDING
NEGLIGENCE OR OTHERWISE) ARISING IN ANY WAY OUT OF THE USE OF THIS
SOFTWARE, EVEN IF ADVISED OF THE POSSIBILITY OF SUCH DAMAGE.


-------------------------------------------------------------------------------

Contains code from mapbox-gl-js v1.13 and earlier

Version v1.13 of mapbox-gl-js and earlier are licensed under a BSD-3-Clause license

Copyright (c) 2020, Mapbox
Redistribution and use in source and binary forms, with or without modification,
are permitted provided that the following conditions are met:

* Redistributions of source code must retain the above copyright notice,
  this list of conditions and the following disclaimer.
* Redistributions in binary form must reproduce the above copyright notice,
  this list of conditions and the following disclaimer in the documentation
  and/or other materials provided with the distribution.
* Neither the name of Mapbox GL JS nor the names of its contributors
  may be used to endorse or promote products derived from this software
  without specific prior written permission.

THIS SOFTWARE IS PROVIDED BY THE COPYRIGHT HOLDERS AND CONTRIBUTORS
"AS IS" AND ANY EXPRESS OR IMPLIED WARRANTIES, INCLUDING, BUT NOT
LIMITED TO, THE IMPLIED WARRANTIES OF MERCHANTABILITY AND FITNESS FOR
A PARTICULAR PURPOSE ARE DISCLAIMED. IN NO EVENT SHALL THE COPYRIGHT OWNER OR
CONTRIBUTORS BE LIABLE FOR ANY DIRECT, INDIRECT, INCIDENTAL, SPECIAL,
EXEMPLARY, OR CONSEQUENTIAL DAMAGES (INCLUDING, BUT NOT LIMITED TO,
PROCUREMENT OF SUBSTITUTE GOODS OR SERVICES; LOSS OF USE, DATA, OR
PROFITS; OR BUSINESS INTERRUPTION) HOWEVER CAUSED AND ON ANY THEORY OF
LIABILITY, WHETHER IN CONTRACT, STRICT LIABILITY, OR TORT (INCLUDING
NEGLIGENCE OR OTHERWISE) ARISING IN ANY WAY OUT OF THE USE OF THIS SOFTWARE,
EVEN IF ADVISED OF THE POSSIBILITY OF SUCH DAMAGE.


-------------------------------------------------------------------------------

Contains code from glfx.js

Copyright (C) 2011 by Evan Wallace

Permission is hereby granted, free of charge, to any person obtaining a copy
of this software and associated documentation files (the "Software"), to deal
in the Software without restriction, including without limitation the rights
to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
copies of the Software, and to permit persons to whom the Software is
furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in
all copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN
THE SOFTWARE.

--------------------------------------------------------------------------------

Contains a portion of d3-color https://github.com/d3/d3-color

Copyright 2010-2016 Mike Bostock
All rights reserved.

Redistribution and use in source and binary forms, with or without modification,
are permitted provided that the following conditions are met:

* Redistributions of source code must retain the above copyright notice, this
  list of conditions and the following disclaimer.

* Redistributions in binary form must reproduce the above copyright notice,
  this list of conditions and the following disclaimer in the documentation
  and/or other materials provided with the distribution.

* Neither the name of the author nor the names of contributors may be used to
  endorse or promote products derived from this software without specific prior
  written permission.

THIS SOFTWARE IS PROVIDED BY THE COPYRIGHT HOLDERS AND CONTRIBUTORS "AS IS" AND
ANY EXPRESS OR IMPLIED WARRANTIES, INCLUDING, BUT NOT LIMITED TO, THE IMPLIED
WARRANTIES OF MERCHANTABILITY AND FITNESS FOR A PARTICULAR PURPOSE ARE
DISCLAIMED. IN NO EVENT SHALL THE COPYRIGHT OWNER OR CONTRIBUTORS BE LIABLE FOR
ANY DIRECT, INDIRECT, INCIDENTAL, SPECIAL, EXEMPLARY, OR CONSEQUENTIAL DAMAGES
(INCLUDING, BUT NOT LIMITED TO, PROCUREMENT OF SUBSTITUTE GOODS OR SERVICES;
LOSS OF USE, DATA, OR PROFITS; OR BUSINESS INTERRUPTION) HOWEVER CAUSED AND ON
ANY THEORY OF LIABILITY, WHETHER IN CONTRACT, STRICT LIABILITY, OR TORT
(INCLUDING NEGLIGENCE OR OTHERWISE) ARISING IN ANY WAY OUT OF THE USE OF THIS
SOFTWARE, EVEN IF ADVISED OF THE POSSIBILITY OF SUCH DAMAGE.
```

## polylabel

`src\VL.Globe\Engine\Symbols\PoleOfInaccessibility.cs` (where a polygon's label goes) follows GL JS's
`find_pole_of_inaccessibility.ts`, which is mapbox/polylabel - https://github.com/mapbox/polylabel:

```
ISC License
Copyright (c) 2026 Mapbox

Permission to use, copy, modify, and/or distribute this software for any purpose
with or without fee is hereby granted, provided that the above copyright notice
and this permission notice appear in all copies.

THE SOFTWARE IS PROVIDED "AS IS" AND ISC DISCLAIMS ALL WARRANTIES WITH REGARD TO
THIS SOFTWARE INCLUDING ALL IMPLIED WARRANTIES OF MERCHANTABILITY AND FITNESS.
IN NO EVENT SHALL ISC BE LIABLE FOR ANY SPECIAL, DIRECT, INDIRECT, OR
CONSEQUENTIAL DAMAGES OR ANY DAMAGES WHATSOEVER RESULTING FROM LOSS OF USE, DATA
OR PROFITS, WHETHER IN AN ACTION OF CONTRACT, NEGLIGENCE OR OTHER TORTIOUS
ACTION, ARISING OUT OF OR IN CONNECTION WITH THE USE OR PERFORMANCE OF THIS
SOFTWARE.
```

## Stride

The prototype of screen-space ambient occlusion in `shaders\GlobeShadowBase.sdsl` (off unless `VLGLOBE_SSAO`
is set) follows the sampler of Stride's `AmbientOcclusionRawAOShader.sdsl` - https://github.com/stride3d/stride:

```
﻿The MIT License (MIT)

Copyright (c) 2021 .NET Foundation and Contributors (https://dotnetfoundation.org/)
Copyright (c) 2018-2021 Stride contributors (https://stride3d.net)  
Copyright (c) 2011-2018 Silicon Studio Corp. (https://www.siliconstudio.co.jp)

All rights reserved.

Permission is hereby granted, free of charge, to any person obtaining a copy
of this software and associated documentation files (the "Software"), to deal
in the Software without restriction, including without limitation the rights
to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
copies of the Software, and to permit persons to whom the Software is
furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all
copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
SOFTWARE.
```

## OSM Liberty, through OpenFreeMap's styles

`help\Assets\liberty-3d.json` is OpenFreeMap's Liberty style - https://github.com/hyperknot/openfreemap-styles
(MIT), a fork of OSM Liberty, https://github.com/maputnik/osm-liberty - with some label, shield and icon
layers removed (`metadata.derived-from` in the file). OSM Liberty's licence, code and design:

## Code License

The Mapbox GL Style JSON file is originally derived from [OSM Bright from Mapbox Open Styles](https://github.com/mapbox/mapbox-gl-styles/blob/master/LICENSE.md). The modified Mapbox GL Style JSON retains the same BSD license.

> Redistribution and use in source and binary forms, with or without modification,
are permitted provided that the following conditions are met:

> * Redistributions of source code must retain the above copyright notice, this list of conditions and the following disclaimer.
* Redistributions in binary form must reproduce the above copyright notice, this list of conditions and the following disclaimer in the documentation and/or other materials provided with the distribution.
* Neither the name of Mapbox nor the names of its contributors may be used to endorse or promote products derived from this software without specific prior written permission.

> THIS SOFTWARE IS PROVIDED BY THE COPYRIGHT HOLDERS AND CONTRIBUTORS "AS IS" AND ANY EXPRESS OR IMPLIED WARRANTIES, INCLUDING, BUT NOT LIMITED TO, THE IMPLIED WARRANTIES OF MERCHANTABILITY AND FITNESS FOR A PARTICULAR PURPOSE ARE DISCLAIMED. IN NO EVENT SHALL THE COPYRIGHT OWNER OR CONTRIBUTORS BE LIABLE FOR ANY DIRECT, INDIRECT, INCIDENTAL, SPECIAL, EXEMPLARY, OR CONSEQUENTIAL DAMAGES (INCLUDING, BUT NOT LIMITED TO, PROCUREMENT OF SUBSTITUTE GOODS OR SERVICES; LOSS OF USE, DATA, OR PROFITS; OR BUSINESS INTERRUPTION) HOWEVER CAUSED AND ON ANY THEORY OF LIABILITY, WHETHER IN CONTRACT, STRICT LIABILITY, OR TORT (INCLUDING NEGLIGENCE OR OTHERWISE) ARISING IN ANY WAY OUT OF THE USE OF THIS SOFTWARE, EVEN IF ADVISED OF THE POSSIBILITY OF SUCH DAMAGE.

## Design License

The look and feel of the OSM liberty map design is also derived (although heavily altered) from [OSM Bright from Mapbox Open Styles](https://github.com/mapbox/mapbox-gl-styles/blob/master/LICENSE.md) which is licensed under the Creative Commons Attribution 3.0 license.

The map is displaying and styling the data from [OpenMapTiles](https://openmaptiles.org/) with [CC-BY 4.0 design license](https://github.com/openmaptiles/openmaptiles/blob/master/LICENSE.md).

The products or services using this map style need to visibly credit "OpenMapTiles.org" or reference "OpenMapTiles" with a link to http://openmaptiles.org/. For a browsable electronic map based on OpenMapTiles and OpenStreetMap data, the credit should appear in the corner of the map. For example:

[© OpenMapTiles](https://openmaptiles.org/)
[© OpenStreetMap contributors](https://www.openstreetmap.org/copyright)

For printed and static maps a similar attribution should be made in a textual description near the image, in the same fashion as if you cite a photograph.

## Icons

OSM Liberty is using the [Maki POI icon set](https://github.com/mapbox/maki/blob/master/LICENSE.txt) which is licensed under CC0 1.0 Universal.

The right arrow was derived from [Wikipedia][wiki_arrow] which is in the public domain.

[wiki_arrow]: https://commons.wikimedia.org/wiki/File:Arrowright.svg

## Fonts

OSM Liberty is using the Roboto font family (Copyright 2011 Google).
Roboto is licensed under the [Apache License, Version 2.0](https://github.com/google/roboto/blob/master/LICENSE).

## Map data and tiles

No map data is part of this package. The help patches fetch tiles from OpenStreetMap
(© OpenStreetMap contributors, ODbL - https://www.openstreetmap.org/copyright) and from OpenFreeMap
(https://openfreemap.org, OpenMapTiles schema, © OpenMapTiles, data © OpenStreetMap contributors); each
service has its own terms, and the credit is printed in the window.

## In this repository, not in the package

- The pictures under `docs\` show map tiles of OpenStreetMap: © OpenStreetMap contributors; the map data
  under the ODbL, the tiles' cartography under CC BY-SA 2.0 (https://www.openstreetmap.org/copyright).
- `test\VL.Globe.Tests\MapLibre\` holds fixtures of MapLibre GL JS and of pixelmatch with their own
  licence files beside them; MapLibre's render tests are fetched by a script and not committed.

The ideas taken from Mapbox GL JS v2 and later (fill-extrusion's ambient occlusion and ground radius,
its cutoff fade, two shadow cascades) come from its published style specification and documentation; none of
its code, which is not under an open licence, is in this repository.
