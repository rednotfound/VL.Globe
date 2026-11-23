using Stride.Engine;

namespace VL.Globe.Engine;

// How Globe learns the eye of a camera-relative camera without a new pin (ROADMAP item 1, step 4).
// GlobeCamera attaches one of these to its camera entity and writes into it every frame, at the same
// moment it builds the view matrix - so the eye here and the rotation in the view are one snapshot,
// however Drag and ZoomByWheel change the GlobeView later in the frame. Globe runs after GlobeCamera
// (its Camera input is GlobeCamera's output), so it always reads this frame's snapshot.
//
// A camera without one (vvvv's OrbitCamera, any camera of the user's) is drawn in absolute
// coordinates as before.
internal sealed class GlobeCameraFrame : EntityComponent
{
    /// <summary>The eye in scene units, in double: the camera's view matrix places it at the origin.</summary>
    public D3 Eye;

    /// <summary>The camera's state, for GlobeWindow given this camera on its Camera pin.</summary>
    public GlobeView? View;
}
