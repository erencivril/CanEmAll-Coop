using System;

namespace CanEmAllCoop.Cans;

/// <summary>
/// "Smallest three" quaternion compression into 32 bits: 2 bits index of the largest component, then the other
/// three as 10-bit values in [-1/sqrt2, 1/sqrt2]. Worst-case error is a fraction of a degree.
/// </summary>
public static class QuatPack
{
    const float Max = 0.70710678f;
    const int Scale = 511;

    public static uint Pack(float x, float y, float z, float w)
    {
        float len = MathF.Sqrt(x * x + y * y + z * z + w * w);
        if (len < 1e-6f) { x = y = z = 0f; w = 1f; len = 1f; }
        var c = new[] { x / len, y / len, z / len, w / len };

        int largest = 0;
        float best = MathF.Abs(c[0]);
        for (int i = 1; i < 4; i++)
        {
            float a = MathF.Abs(c[i]);
            if (a > best) { best = a; largest = i; }
        }

        // q and -q are the same rotation: make the dropped component positive so it can be reconstructed.
        float sign = c[largest] < 0f ? -1f : 1f;

        uint packed = (uint)largest << 30;
        int shift = 20;
        for (int i = 0; i < 4; i++)
        {
            if (i == largest) continue;
            float v = Math.Clamp(c[i] * sign, -Max, Max);
            int q = (int)MathF.Round(v / Max * Scale); // -511..511
            packed |= ((uint)(q + Scale) & 0x3FFu) << shift; // bias into 0..1022
            shift -= 10;
        }
        return packed;
    }

    public static void Unpack(uint packed, out float x, out float y, out float z, out float w)
    {
        int largest = (int)(packed >> 30);
        var c = new float[4];
        float sumSq = 0f;
        int shift = 20;
        for (int i = 0; i < 4; i++)
        {
            if (i == largest) continue;
            int q = (int)((packed >> shift) & 0x3FFu) - Scale;
            float v = q / (float)Scale * Max;
            c[i] = v;
            sumSq += v * v;
            shift -= 10;
        }
        c[largest] = MathF.Sqrt(MathF.Max(0f, 1f - sumSq));
        x = c[0]; y = c[1]; z = c[2]; w = c[3];
    }
}
