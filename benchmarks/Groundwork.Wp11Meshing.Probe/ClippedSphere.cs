using System.Numerics;

sealed class ClippedSphere
{
    private readonly CollisionSphere _solid;
    private readonly CollisionSphere _cut;
    private readonly Vector3 _axis;
    private readonly Vector3 _rimCenter;
    private readonly float _rimRadius;

    private ClippedSphere(CollisionSphere solid, CollisionSphere cut, float separation)
    {
        _solid = solid;
        _cut = cut;
        _axis = (cut.Center - solid.Center) / separation;
        var along = (solid.Radius * solid.Radius - cut.Radius * cut.Radius + separation * separation) /
            (2 * separation);
        _rimCenter = solid.Center + along * _axis;
        _rimRadius = MathF.Sqrt(Math.Max(0, solid.Radius * solid.Radius - along * along));
    }

    internal static ClippedSphere? Create(CollisionSphere solid, CollisionSphere cut)
    {
        var separation = Vector3.Distance(solid.Center, cut.Center);
        return separation > MathF.Abs(solid.Radius - cut.Radius) && separation < solid.Radius + cut.Radius
            ? new ClippedSphere(solid, cut, separation) : null;
    }

    internal float Distance(Vector3 point)
    {
        var fromSolid = point - _solid.Center;
        var fromCut = point - _cut.Center;
        var solidDistance = fromSolid.Length();
        var cutDistance = fromCut.Length();
        if (solidDistance <= _solid.Radius && cutDistance >= _cut.Radius)
            return 0;

        // The closest point is either a valid radial projection on a
        // spherical face or a point on the shared circular edge.
        var fromRim = point - _rimCenter;
        var axial = Vector3.Dot(fromRim, _axis);
        var radial = (fromRim - axial * _axis).Length() - _rimRadius;
        var distance = MathF.Sqrt(axial * axial + radial * radial);
        if (solidDistance > 0)
        {
            var projected = _solid.Center + fromSolid * (_solid.Radius / solidDistance);
            if (Vector3.DistanceSquared(projected, _cut.Center) >= _cut.Radius * _cut.Radius)
                distance = Math.Min(distance, MathF.Abs(solidDistance - _solid.Radius));
        }
        if (cutDistance > 0)
        {
            var projected = _cut.Center + fromCut * (_cut.Radius / cutDistance);
            if (Vector3.DistanceSquared(projected, _solid.Center) <= _solid.Radius * _solid.Radius)
                distance = Math.Min(distance, MathF.Abs(cutDistance - _cut.Radius));
        }
        return distance;
    }
}
