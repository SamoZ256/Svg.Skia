namespace Svg
{
    /// <summary>
    /// A gradient that turns around a centre, Skia's sweep gradient. Not in any SVG specification,
    /// so browsers ignore it and paint the fallback colour written after <c>url(#id)</c>.
    /// </summary>
    [SvgElement("sweepGradient")]
    public partial class SvgSweepGradientServer : SvgGradientServer
    {
        [SvgAttribute("cx")]
        public SvgUnit CenterX
        {
            get { return GetAttribute("cx", false, SvgDeferredPaintServer.TryGet<SvgSweepGradientServer>(InheritGradient, null)?.CenterX ?? new SvgUnit(SvgUnitType.Percentage, 50f)); }
            set { Attributes["cx"] = value; }
        }

        [SvgAttribute("cy")]
        public SvgUnit CenterY
        {
            get { return GetAttribute("cy", false, SvgDeferredPaintServer.TryGet<SvgSweepGradientServer>(InheritGradient, null)?.CenterY ?? new SvgUnit(SvgUnitType.Percentage, 50f)); }
            set { Attributes["cy"] = value; }
        }

        /// <summary>Degrees clockwise from the gradient space's +x axis, where offset 0 sits.</summary>
        [SvgAttribute("startAngle")]
        public float StartAngle
        {
            get { return GetAttribute("startAngle", false, SvgDeferredPaintServer.TryGet<SvgSweepGradientServer>(InheritGradient, null)?.StartAngle ?? 0f); }
            set { Attributes["startAngle"] = value; }
        }

        /// <summary>Degrees clockwise from the gradient space's +x axis, where offset 1 sits.</summary>
        [SvgAttribute("endAngle")]
        public float EndAngle
        {
            get { return GetAttribute("endAngle", false, SvgDeferredPaintServer.TryGet<SvgSweepGradientServer>(InheritGradient, null)?.EndAngle ?? 360f); }
            set { Attributes["endAngle"] = value; }
        }

        public override SvgElement DeepCopy()
        {
            return DeepCopy<SvgSweepGradientServer>();
        }
    }
}
