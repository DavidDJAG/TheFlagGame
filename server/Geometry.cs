namespace TheFlag.Server;

public static class Geometry
{
    public static float Length(Vec2 v) => MathF.Sqrt(v.X * v.X + v.Y * v.Y);

    public static Vec2 Normalize(Vec2 v)
    {
        var length = Length(v);
        if (length < 0.0001f)
        {
            return new Vec2(0f, 0f);
        }

        return new Vec2(v.X / length, v.Y / length);
    }

    public static float Dot(Vec2 a, Vec2 b) => a.X * b.X + a.Y * b.Y;

    public static float Cross(Vec2 a, Vec2 b) => a.X * b.Y - a.Y * b.X;

    public static float DistanceSquared(Vec2 a, Vec2 b)
    {
        var dx = a.X - b.X;
        var dy = a.Y - b.Y;
        return dx * dx + dy * dy;
    }

    public static float DistancePointToSegment(Vec2 point, Vec2 a, Vec2 b)
    {
        var ab = new Vec2(b.X - a.X, b.Y - a.Y);
        var ap = new Vec2(point.X - a.X, point.Y - a.Y);
        var abLengthSq = ab.X * ab.X + ab.Y * ab.Y;
        if (abLengthSq <= 0.0001f)
        {
            return MathF.Sqrt(DistanceSquared(point, a));
        }

        var t = Math.Clamp((ap.X * ab.X + ap.Y * ab.Y) / abLengthSq, 0f, 1f);
        var closest = new Vec2(a.X + ab.X * t, a.Y + ab.Y * t);
        return MathF.Sqrt(DistanceSquared(point, closest));
    }

    public static bool PointInPolygon(Vec2 point, List<Vec2> polygon)
    {
        var inside = false;
        for (int i = 0, j = polygon.Count - 1; i < polygon.Count; j = i++)
        {
            var pi = polygon[i];
            var pj = polygon[j];
            var intersect = ((pi.Y > point.Y) != (pj.Y > point.Y)) &&
                            (point.X < (pj.X - pi.X) * (point.Y - pi.Y) / ((pj.Y - pi.Y) + 0.00001f) + pi.X);
            if (intersect)
            {
                inside = !inside;
            }
        }

        return inside;
    }

    public static bool CircleIntersectsRect(Vec2 center, float radius, ObstacleShape rect)
    {
        var nearestX = Math.Clamp(center.X, rect.Position.X, rect.Position.X + rect.Width);
        var nearestY = Math.Clamp(center.Y, rect.Position.Y, rect.Position.Y + rect.Height);
        var dx = center.X - nearestX;
        var dy = center.Y - nearestY;
        return (dx * dx + dy * dy) <= radius * radius;
    }

    public static bool CircleIntersectsCircle(Vec2 center, float radius, ObstacleShape circle)
    {
        var totalRadius = radius + circle.Radius;
        return DistanceSquared(center, circle.Position) <= totalRadius * totalRadius;
    }

    public static bool CircleIntersectsPolygon(Vec2 center, float radius, List<Vec2> polygon)
    {
        if (PointInPolygon(center, polygon))
        {
            return true;
        }

        for (var i = 0; i < polygon.Count; i++)
        {
            var a = polygon[i];
            var b = polygon[(i + 1) % polygon.Count];
            if (DistancePointToSegment(center, a, b) <= radius)
            {
                return true;
            }
        }

        return false;
    }

    public static bool IsCircleInsidePerimeter(Vec2 center, float radius, List<Vec2> perimeter)
    {
        if (!PointInPolygon(center, perimeter))
        {
            return false;
        }

        for (var i = 0; i < perimeter.Count; i++)
        {
            var a = perimeter[i];
            var b = perimeter[(i + 1) % perimeter.Count];
            if (DistancePointToSegment(center, a, b) < radius)
            {
                return false;
            }
        }

        return true;
    }

    public static bool RayIntersectsCircleDistance(Vec2 origin, Vec2 direction, Vec2 center, float radius, out float distance)
    {
        distance = 0f;
        var toCenter = center - origin;
        var projection = Dot(toCenter, direction);
        var centerDistanceSq = Dot(toCenter, toCenter);
        var radiusSq = radius * radius;
        var perpendicularSq = centerDistanceSq - projection * projection;
        if (perpendicularSq > radiusSq)
        {
            return false;
        }

        var offset = MathF.Sqrt(MathF.Max(0f, radiusSq - perpendicularSq));
        var near = projection - offset;
        var far = projection + offset;
        var hit = near >= 0.001f ? near : far;
        if (hit < 0.001f)
        {
            return false;
        }

        distance = hit;
        return true;
    }

    public static bool RayIntersectsSegmentDistance(Vec2 origin, Vec2 direction, Vec2 a, Vec2 b, out float distance)
    {
        distance = 0f;
        var segment = b - a;
        var cross = Cross(direction, segment);
        if (MathF.Abs(cross) < 0.0001f)
        {
            return false;
        }

        var delta = a - origin;
        var t = Cross(delta, segment) / cross;
        var u = Cross(delta, direction) / cross;
        if (t < 0.001f || u < 0f || u > 1f)
        {
            return false;
        }

        distance = t;
        return true;
    }

    public static bool RayIntersectsRectDistance(Vec2 origin, Vec2 direction, ObstacleShape rect, out float distance)
    {
        distance = float.MaxValue;
        var p1 = rect.Position;
        var p2 = new Vec2(rect.Position.X + rect.Width, rect.Position.Y);
        var p3 = new Vec2(rect.Position.X + rect.Width, rect.Position.Y + rect.Height);
        var p4 = new Vec2(rect.Position.X, rect.Position.Y + rect.Height);

        var hit = false;
        hit |= ConsiderRaySegment(origin, direction, p1, p2, ref distance);
        hit |= ConsiderRaySegment(origin, direction, p2, p3, ref distance);
        hit |= ConsiderRaySegment(origin, direction, p3, p4, ref distance);
        hit |= ConsiderRaySegment(origin, direction, p4, p1, ref distance);
        return hit;
    }

    public static bool RayIntersectsPolygonDistance(Vec2 origin, Vec2 direction, List<Vec2> polygon, out float distance)
    {
        distance = float.MaxValue;
        var hit = false;
        for (var i = 0; i < polygon.Count; i++)
        {
            var a = polygon[i];
            var b = polygon[(i + 1) % polygon.Count];
            hit |= ConsiderRaySegment(origin, direction, a, b, ref distance);
        }

        return hit;
    }

    private static bool ConsiderRaySegment(Vec2 origin, Vec2 direction, Vec2 a, Vec2 b, ref float bestDistance)
    {
        if (!RayIntersectsSegmentDistance(origin, direction, a, b, out var candidate))
        {
            return false;
        }

        if (candidate < bestDistance)
        {
            bestDistance = candidate;
        }

        return true;
    }
}
