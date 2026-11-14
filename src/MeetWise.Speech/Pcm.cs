namespace MeetWise.Speech;

/// <summary>Converts raw WASAPI buffers (32-bit float or 16-bit PCM) to float samples.</summary>
public static class Pcm
{
    public static float[] ToFloat(byte[] buffer, int count, bool isFloat32)
    {
        if (isFloat32)
        {
            var f = new float[count / 4];
            Buffer.BlockCopy(buffer, 0, f, 0, f.Length * 4);
            return f;
        }
        var s = new float[count / 2];
        for (int i = 0; i < s.Length; i++)
            s[i] = BitConverter.ToInt16(buffer, i * 2) / 32768f;
        return s;
    }
}
