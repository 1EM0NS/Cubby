namespace Cubby.Shell.Diagnostics;

/// <summary>一个待探测的屏幕采样点。坐标为虚拟屏幕物理像素。</summary>
/// <param name="ExpectOurs">true 表示该点落在盒子内，期望由浮层拦截；false 表示在盒子外，期望放行。</param>
public sealed record SamplePoint(string Label, int X, int Y, bool ExpectOurs, string Note);