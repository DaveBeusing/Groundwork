using System.Numerics;

internal sealed class GroundedRoute
{
    private const float MaxStep = .55f;
    private Vector3 _position = new(8.3f, 16, 15.2f);

    internal int Steps { get; private set; }
    internal Vector3 Position => _position;

    internal bool TryTraverse(Mesh mesh, Vector2 destination)
    {
        var start = new Vector2(_position.X, _position.Z);
        var steps = Math.Max(1, (int)MathF.Ceiling(Vector2.Distance(start, destination) / .35f));
        var next = _position;
        for (var i = 1; i <= steps; i++)
        {
            var point = Vector2.Lerp(start, destination, i / (float)steps);
            var origin = new Vector3(point.X, 26, point.Y);
            var hits = GeometryProbe.MeshHits(mesh, origin, -Vector3.UnitY);
            if (hits.Count == 0 || MathF.Abs(origin.Y - hits[0] - next.Y) > MaxStep)
                return false;
            next = new Vector3(point.X, origin.Y - hits[0], point.Y);
        }

        _position = next;
        Steps += steps;
        return true;
    }
}
