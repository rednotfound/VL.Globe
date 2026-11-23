using System;
using Stride.Core.Mathematics;
using VL.Core;
using VL.Core.Import;
using VL.Globe;

namespace VL;

// The /// text is the tooltip; the reasoning is here.
//
// ROADMAP 0.1 item 2: VL.Mapsui's ScreenToWorld, WorldToScreen and ViewportInfo, for the globe. Same
// shape - static nodes reading the view, pure maths, nothing held - with two differences a sphere
// forces:
//   - positions are in PROJECTION space (-1..1, y up), the Mouse's Position in Projection, as Drag
//     and ZoomByWheel take them; no pixel size is needed for the conversion.
//   - a screen position can miss the globe (ScreenToGlobe's Success), and a place on the globe can
//     be on its far side or behind the camera (GlobeToScreen's Visible). Mapsui's flat map has neither.
// They read the GlobeView as it is when they run: after Drag/ZoomByWheel/DragRotate if wired after
// them, which is the view the next frame is drawn from.
//
// Heights are above sea level; on the smooth sphere of 0.1 that is the surface (terrain is 0.2).

/// <summary>Converts between screen positions and longitude/latitude on a globe, and reads where it looks.</summary>
[Name("Globe")]
public static class GlobeViewNodes
{
    /// <summary>The longitude and latitude under a screen position; Success is false when it misses the globe.</summary>
    /// <param name="view">From GlobeCamera or GlobeWindow.</param>
    /// <param name="position">In projection space, -1..1 - the Mouse's Position in Projection.</param>
    /// <param name="longitude">Degrees east.</param>
    /// <param name="latitude">Degrees north.</param>
    /// <param name="success">False when the position looks past the globe into space.</param>
    public static void ScreenToGlobe(GlobeView? view, Vector2 position, out double longitude, out double latitude, out bool success)
    {
        longitude = latitude = 0;
        success = false;
        if (view?.PickDir(view.CameraDirectionFromProjection(position.X, position.Y)) is not { } p) return;
        longitude = Math.Atan2(p.X, p.Z) * 180 / Math.PI;
        latitude = Math.Asin(Math.Clamp(p.Y, -1, 1)) * 180 / Math.PI;
        success = true;
    }

    // GlobeWindow's own, in the NodeBrowser's advanced list only: the camera wired to its Camera pin, else the one it keeps, and that camera's view
    // (a GlobeCamera's; null for any other camera). DESIGN-NODES step 2, 2026-11-23.
    /// <summary>The camera GlobeWindow shows the globe with: the one wired to its Camera pin, else its own.</summary>
    [Smell(SymbolSmell.Advanced)]   // Hidden made it unresolvable inside VL.Globe.vl itself (2026-11-23)
    public static global::Stride.Engine.Entity? GlobeWindowCamera(global::Stride.Engine.Entity? wired, global::Stride.Engine.Entity? own, out GlobeView? view)
    {
        var camera = wired ?? own;
        view = camera?.Get<VL.Globe.Engine.Camera.GlobeCameraFrame>()?.View;
        return camera;
    }

    /// <summary>Where on the screen a longitude/latitude is drawn; Visible is false behind the globe or the camera.</summary>
    /// <param name="view">From GlobeCamera or GlobeWindow.</param>
    /// <param name="position">In projection space, -1..1 inside the window, y up.</param>
    /// <param name="visible">False when the place is on the globe's far side or behind the camera. It may still be outside the window.</param>
    /// <param name="longitude">Degrees east.</param>
    /// <param name="latitude">Degrees north.</param>
    /// <param name="height">Metres above sea level.</param>
    public static void GlobeToScreen(GlobeView? view, out Vector2 position, out bool visible, double longitude = 0, double latitude = 0, double height = 0)
    {
        position = Vector2.Zero;
        visible = false;
        if (view is null) return;
        var (eye, fwd, up) = view.Pose();
        var right = D3.Cross(fwd, up).Normalized;
        var unit = GlobeView.Unit(longitude, latitude);
        var p = unit * (view.Radius * (1 + height / GlobeCoordinates.EarthRadius));
        var d = p - eye;
        double z = D3.Dot(d, fwd);
        if (z <= 0) return;                                   // behind the camera
        position = new Vector2((float)(D3.Dot(d, right) / z * view.ProjX), (float)(D3.Dot(d, up) / z * view.ProjY));
        // hidden by the globe when the sight line from the eye meets the sphere before the point
        double b = D3.Dot(eye, d) / d.Length, c = D3.Dot(eye, eye) - view.Radius * (double)view.Radius;
        double disc = b * b - c;
        double hit = disc < 0 ? double.PositiveInfinity : -b - Math.Sqrt(disc);
        visible = !(hit > 0 && hit < d.Length * (1 - 1e-9));
    }

    /// <summary>Where a globe camera looks from and how much ground one pixel covers there.</summary>
    /// <param name="view">From GlobeCamera or GlobeWindow.</param>
    /// <param name="centerLongitude">Degrees east of the point at the centre of the view.</param>
    /// <param name="centerLatitude">Degrees north of the point at the centre of the view.</param>
    /// <param name="height">Metres the camera is above the surface.</param>
    /// <param name="heading">Degrees clockwise from north that the top of the view points to.</param>
    /// <param name="tilt">Degrees from looking straight down.</param>
    /// <param name="resolution">Ground metres one pixel covers at the centre.</param>
    /// <param name="zoom">The web-map zoom level with that resolution - the one VL.Mapsui shows for the same ground.</param>
    public static void GlobeViewInfo(GlobeView? view, out double centerLongitude, out double centerLatitude, out double height,
        out double heading, out double tilt, out double resolution, out double zoom)
    {
        centerLongitude = centerLatitude = height = heading = tilt = resolution = zoom = 0;
        if (view is null) return;
        centerLongitude = view.Longitude;
        centerLatitude = view.Latitude;
        height = view.EyeAltitude;
        heading = view.Heading;
        tilt = view.Tilt;
        resolution = view.GroundResolution();
        zoom = GlobeView.ZoomOf(resolution, view.Latitude);
    }
}
