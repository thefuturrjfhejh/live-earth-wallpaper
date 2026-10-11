// Live Earth wallpaper - ray-traces a sun-lit globe for the current moment once a minute
// and hands the frame to Windows as the desktop wallpaper. Sleeps (0% CPU) between frames.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.Globalization;
using System.IO;
using System.Net;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32;

namespace LiveEarth
{
    static class Native
    {
        [DllImport("user32.dll")] public static extern bool SetProcessDPIAware();
        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        public static extern bool SystemParametersInfo(uint action, uint param, string vparam, uint winIni);
        [DllImport("shell32.dll")] public static extern int SHQueryUserNotificationState(out int state);
        [DllImport("kernel32.dll")] public static extern IntPtr GetCurrentProcess();
        [DllImport("kernel32.dll")] public static extern bool SetProcessInformation(IntPtr h, int cls, ref PowerThrottling info, int size);
        [DllImport("user32.dll")] public static extern bool DestroyIcon(IntPtr h);
        [StructLayout(LayoutKind.Sequential)]
        public struct PowerThrottling { public uint Version; public uint ControlMask; public uint StateMask; }

        public const uint SPI_SETDESKWALLPAPER = 0x14;
        public const uint SPIF_UPDATEINIFILE = 1, SPIF_SENDCHANGE = 2;

        // Windows 11 "EcoQoS": the scheduler keeps this process on low clocks / efficient cores.
        public static void EnableEcoMode()
        {
            try
            {
                var s = new PowerThrottling { Version = 1, ControlMask = 1, StateMask = 1 };
                SetProcessInformation(GetCurrentProcess(), 4, ref s, Marshal.SizeOf(typeof(PowerThrottling)));
            }
            catch { }
            try { Process.GetCurrentProcess().PriorityClass = ProcessPriorityClass.Idle; } catch { }
        }
    }

    static class Log
    {
        public static string PathName;
        public static void W(string msg)
        {
            try
            {
                if (PathName == null) return;
                var fi = new FileInfo(PathName);
                if (fi.Exists && fi.Length > 512 * 1024) fi.Delete();
                File.AppendAllText(PathName, DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + "  " + msg + "\r\n");
            }
            catch { }
        }
    }

    class Config
    {
        public string FilePath;
        public string Place = "";            // empty = no marker / sunrise line until the user sets their city
        public double Lat = 0, Lon = 0;
        public bool HasPlace { get { return !string.IsNullOrEmpty(Place); } }
        public string Mode = "sun";          // sun = view fixed relative to the Sun (Earth visibly spins); home = parked above Place
        public double Phase = 55;            // degrees the camera sits east of the subsolar point (evening terminator on the right)
        public double CamLat = 18;           // camera latitude in sun mode
        public double Globe = 0.80;          // globe diameter as a fraction of screen height
        public double CenterX = 0.50, CenterY = 0.47;
        public int Interval = 60;            // seconds between frames
        public bool Clouds = true;
        public bool ShowText = true;
        public bool ShowMarker = true;
        public int CpuLimit = 75;            // skip a frame while the laptop is busier than this (%)
        public bool LiveLights = true;       // last night's real satellite city lights instead of the 2016 composite

        static double D(string s, double def) { double v; return double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out v) ? v : def; }
        static bool B(string s, bool def) { s = s.Trim().ToLowerInvariant(); if (s == "1" || s == "true" || s == "on" || s == "yes") return true; if (s == "0" || s == "false" || s == "off" || s == "no") return false; return def; }

        public static Config Load(string path)
        {
            var c = new Config { FilePath = path };
            if (!File.Exists(path)) { c.Save(); return c; }
            foreach (var raw in File.ReadAllLines(path))
            {
                var line = raw.Trim();
                if (line.Length == 0 || line.StartsWith("#") || line.StartsWith(";")) continue;
                c.Apply(line);
            }
            return c;
        }

        // "key=value" - shared by the settings file and the --set command-line override
        public void Apply(string line)
        {
            int eq = line.IndexOf('='); if (eq < 0) return;
            string k = line.Substring(0, eq).Trim().ToLowerInvariant(), v = line.Substring(eq + 1).Trim();
            switch (k)
            {
                case "place": Place = v; break;
                case "lat": Lat = D(v, Lat); break;
                case "lon": Lon = D(v, Lon); break;
                case "mode": Mode = v.ToLowerInvariant() == "home" ? "home" : "sun"; break;
                case "phase": Phase = D(v, Phase); break;
                case "camlat": CamLat = D(v, CamLat); break;
                case "globe": Globe = Math.Max(0.3, Math.Min(1.2, D(v, Globe))); break;
                case "centerx": CenterX = D(v, CenterX); break;
                case "centery": CenterY = D(v, CenterY); break;
                case "interval": Interval = Math.Max(30, (int)D(v, Interval)); break;
                case "clouds": Clouds = B(v, Clouds); break;
                case "showtext": ShowText = B(v, ShowText); break;
                case "showmarker": ShowMarker = B(v, ShowMarker); break;
                case "cpulimit": CpuLimit = (int)D(v, CpuLimit); break;
                case "lights": LiveLights = v.Trim().ToLowerInvariant() != "2016"; break;
            }
        }

        public void Save()
        {
            var ci = CultureInfo.InvariantCulture;
            var sb = new StringBuilder();
            sb.AppendLine("# Live Earth settings - edit, then choose 'Refresh now' from the tray icon.");
            sb.AppendLine("# Your city: name + coordinates (Google Maps: right-click your city, the first line is lat, lon).");
            sb.AppendLine("# Example:  place=Kolkata   lat=22.57   lon=88.36   (south latitudes and west longitudes are negative)");
            sb.AppendLine("# Leave place empty to hide the marker and the sunrise/sunset line.");
            sb.AppendLine("place=" + Place);
            sb.AppendLine("lat=" + Lat.ToString(ci));
            sb.AppendLine("lon=" + Lon.ToString(ci));
            sb.AppendLine("# mode=sun : camera fixed relative to the Sun, so you watch the Earth spin under the day/night line");
            sb.AppendLine("# mode=home: camera parked above your place, so you watch day and night sweep over it");
            sb.AppendLine("mode=" + Mode);
            sb.AppendLine("# sun mode: degrees east of the noon point (bigger = more night side), and camera latitude");
            sb.AppendLine("phase=" + Phase.ToString(ci));
            sb.AppendLine("camlat=" + CamLat.ToString(ci));
            sb.AppendLine("# globe size (fraction of screen height) and position (fractions of the screen)");
            sb.AppendLine("globe=" + Globe.ToString(ci));
            sb.AppendLine("centerx=" + CenterX.ToString(ci));
            sb.AppendLine("centery=" + CenterY.ToString(ci));
            sb.AppendLine("# seconds between frames (the Earth turns 0.25 degrees per minute)");
            sb.AppendLine("interval=" + Interval.ToString(ci));
            sb.AppendLine("clouds=" + (Clouds ? "on" : "off"));
            sb.AppendLine("showtext=" + (ShowText ? "on" : "off"));
            sb.AppendLine("showmarker=" + (ShowMarker ? "on" : "off"));
            sb.AppendLine("# skip a frame while total CPU use is above this percent (keeps a busy laptop cool)");
            sb.AppendLine("cpulimit=" + CpuLimit.ToString(ci));
            sb.AppendLine("# lights=live : last night's real city lights from NASA's VIIRS satellites (refreshed daily)");
            sb.AppendLine("# lights=2016 : NASA's cloud-free 2016 Black Marble composite");
            sb.AppendLine("lights=" + (LiveLights ? "live" : "2016"));
            try { File.WriteAllText(FilePath, sb.ToString()); } catch { }
        }
    }

    // Equirectangular texture, resampled at load so every layer shares one size (one UV computation per pixel).
    class Tex
    {
        public int W, H, C; public byte[] D;

        public static Tex Load(string path, int w, int h, int channels)
        {
            var bytes = File.ReadAllBytes(path);
            using (var ms = new MemoryStream(bytes))
            using (var src = new Bitmap(ms))
            using (var dst = new Bitmap(w, h, PixelFormat.Format24bppRgb))
            {
                using (var g = Graphics.FromImage(dst))
                using (var ia = new ImageAttributes())
                {
                    g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                    g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                    g.CompositingMode = CompositingMode.SourceCopy;
                    ia.SetWrapMode(WrapMode.TileFlipXY);
                    g.DrawImage(src, new Rectangle(0, 0, w, h), 0, 0, src.Width, src.Height, GraphicsUnit.Pixel, ia);
                }
                var bd = dst.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.ReadOnly, PixelFormat.Format24bppRgb);
                var row = new byte[bd.Stride];
                var t = new Tex { W = w, H = h, C = channels, D = new byte[w * h * channels] };
                for (int y = 0; y < h; y++)
                {
                    Marshal.Copy(new IntPtr(bd.Scan0.ToInt64() + (long)y * bd.Stride), row, 0, bd.Stride);
                    int o = y * w * channels;
                    if (channels == 3)
                        for (int x = 0; x < w; x++) { t.D[o + x * 3] = row[x * 3 + 2]; t.D[o + x * 3 + 1] = row[x * 3 + 1]; t.D[o + x * 3 + 2] = row[x * 3]; }
                    else
                        for (int x = 0; x < w; x++) t.D[o + x] = row[x * 3 + 1];
                }
                dst.UnlockBits(bd);
                return t;
            }
        }
    }

    static class Sun
    {
        static double N360(double a) { a %= 360; return a < 0 ? a + 360 : a; }
        static double N180(double a) { a = N360(a); return a > 180 ? a - 360 : a; }

        // Low-precision solar ephemeris (NOAA/Astronomical Almanac), good to ~0.01 deg - plenty for a wallpaper.
        public static void Position(DateTime utc, out double declRad, out double subLonDeg, out double eotMin)
        {
            double jd = utc.ToOADate() + 2415018.5;
            double n = jd - 2451545.0;
            double L = N360(280.460 + 0.9856474 * n);
            double g = N360(357.528 + 0.9856003 * n) * Math.PI / 180;
            double lam = (L + 1.915 * Math.Sin(g) + 0.020 * Math.Sin(2 * g)) * Math.PI / 180;
            double eps = (23.439 - 0.0000004 * n) * Math.PI / 180;
            double ra = Math.Atan2(Math.Cos(eps) * Math.Sin(lam), Math.Cos(lam)) * 180 / Math.PI;
            declRad = Math.Asin(Math.Sin(eps) * Math.Sin(lam));
            double gmst = N360(280.46061837 + 360.98564736629 * n);
            subLonDeg = N180(ra - gmst);
            eotMin = 4 * N180(L - ra);
        }

        // Sunrise/sunset (UTC) for a local calendar date; false when the sun never rises/sets that day.
        public static bool RiseSet(DateTime localDate, double lat, double lon, out DateTime riseUtc, out DateTime setUtc)
        {
            var noonLocal = new DateTime(localDate.Year, localDate.Month, localDate.Day, 12, 0, 0, DateTimeKind.Local);
            double decl, sl, eot; Position(noonLocal.ToUniversalTime(), out decl, out sl, out eot);
            double phi = lat * Math.PI / 180;
            double cosH = (Math.Sin(-0.833 * Math.PI / 180) - Math.Sin(phi) * Math.Sin(decl)) / (Math.Cos(phi) * Math.Cos(decl));
            var day0 = noonLocal.ToUniversalTime().Date;
            double noonMin = 720 - 4 * lon - eot;
            // keep solar noon on the same local day
            var noonUtc = day0.AddMinutes(noonMin);
            while ((noonUtc.ToLocalTime().Date - localDate.Date).TotalDays > 0.5) noonUtc = noonUtc.AddDays(-1);
            while ((localDate.Date - noonUtc.ToLocalTime().Date).TotalDays > 0.5) noonUtc = noonUtc.AddDays(1);
            riseUtc = setUtc = noonUtc;
            if (cosH < -1 || cosH > 1) return false;
            double H = Math.Acos(cosH) * 180 / Math.PI;
            riseUtc = DateTime.SpecifyKind(noonUtc.AddMinutes(-4 * H), DateTimeKind.Utc);
            setUtc = DateTime.SpecifyKind(noonUtc.AddMinutes(4 * H), DateTimeKind.Utc);
            return true;
        }
    }

    class Renderer
    {
        public Tex Day, Night, Cloud, Spec;
        public const int TW = 3600, TH = 1800;
        readonly float[] lin = new float[256];
        readonly byte[] enc = new byte[4097];
        byte[] stars; int starW, starH, starStride;

        public Renderer()
        {
            for (int i = 0; i < 256; i++) { double c = i / 255.0; lin[i] = (float)(c <= 0.04045 ? c / 12.92 : Math.Pow((c + 0.055) / 1.055, 2.4)); }
            for (int i = 0; i <= 4096; i++) { double c = i / 4096.0; c = c <= 0.0031308 ? c * 12.92 : 1.055 * Math.Pow(c, 1 / 2.4) - 0.055; enc[i] = (byte)Math.Max(0, Math.Min(255, Math.Round(c * 255))); }
        }

        static double SS(double e0, double e1, double x) { double t = (x - e0) / (e1 - e0); t = t < 0 ? 0 : t > 1 ? 1 : t; return t * t * (3 - 2 * t); }
        static double Mix(double a, double b, double t) { return a + (b - a) * t; }

        // The live map is infrared-derived: warm ocean reads grey and cold polar ground reads white.
        // Raise the threshold toward the poles so only real cloud decks survive.
        static double CloudAlpha(double v, double sinLat)
        {
            double pole = SS(0.62, 0.95, Math.Abs(sinLat));
            double lo = Mix(0.40, 0.80, pole), hi = Mix(0.88, 0.99, pole);
            double a = SS(lo, hi, v);
            return a * Mix(0.97, 0.75, pole);
        }

        // Soft shoulder: linear up to 0.62, then rolls off smoothly toward 1 (keeps night lights and dark oceans intact).
        byte Out(double x)
        {
            const double a = 0.62;
            if (x > a) x = a + (1 - a) * (1 - Math.Exp(-(x - a) / (1 - a)));
            if (x < 0) x = 0;
            int i = (int)(x * 4096 + 0.5); if (i > 4096) i = 4096;
            return enc[i];
        }

        void BuildStars(int w, int h, int stride)
        {
            starW = w; starH = h; starStride = stride;
            var acc = new float[w * h * 3];
            var rnd = new Random(1969);
            int count = (int)(w * h / 950.0);
            for (int s = 0; s < count; s++)
            {
                double x = rnd.NextDouble() * w, y = rnd.NextDouble() * h;
                double m = Math.Pow(rnd.NextDouble(), 9);              // most stars faint, a few bright
                double b = 0.006 + m * 0.55;
                double temp = rnd.NextDouble();
                double cr = temp < 0.25 ? 0.75 : temp > 0.85 ? 1.0 : 0.92, cg = temp > 0.85 ? 0.85 : 0.9, cb = temp < 0.25 ? 1.0 : temp > 0.85 ? 0.62 : 0.95;
                double rad = 0.55 + m * 1.4;
                int r = (int)Math.Ceiling(rad * 2.2);
                for (int yy = (int)y - r; yy <= (int)y + r; yy++)
                    for (int xx = (int)x - r; xx <= (int)x + r; xx++)
                    {
                        if (xx < 0 || yy < 0 || xx >= w || yy >= h) continue;
                        double dx = xx + 0.5 - x, dy = yy + 0.5 - y;
                        double v = b * Math.Exp(-(dx * dx + dy * dy) / (2 * rad * rad * 0.36));
                        int o = (yy * w + xx) * 3;
                        acc[o] += (float)(v * cr); acc[o + 1] += (float)(v * cg); acc[o + 2] += (float)(v * cb);
                    }
            }
            stars = new byte[stride * h];
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                {
                    int o = (y * w + x) * 3, p = y * stride + x * 3;
                    stars[p] = Out(acc[o + 2] + 0.0006); stars[p + 1] = Out(acc[o + 1] + 0.0005); stars[p + 2] = Out(acc[o] + 0.0008);
                }
        }

        public class Frame
        {
            public Bitmap Image;
            public double MarkerX, MarkerY, MarkerAlpha;   // screen position of home marker (alpha 0 = hidden)
            public double SubLat, SubLon, HomeSunAlt;
            public long Ms;
        }

        public Frame Render(DateTime utc, Config cfg, Rectangle screen)
        {
            var sw = Stopwatch.StartNew();
            int W = screen.Width, H = screen.Height;
            int stride = ((W * 3 + 3) / 4) * 4;
            if (stars == null || starW != W || starH != H) BuildStars(W, H, stride);
            var buf = (byte[])stars.Clone();

            double decl, subLon, eot;
            Sun.Position(utc, out decl, out subLon, out eot);
            double sLon = subLon * Math.PI / 180;
            double Lx = Math.Cos(decl) * Math.Cos(sLon), Ly = Math.Cos(decl) * Math.Sin(sLon), Lz = Math.Sin(decl);

            // camera
            double camLat, camLon;
            if (cfg.Mode == "home") { camLat = Math.Max(-60, Math.Min(60, cfg.Lat)); camLon = cfg.Lon; }
            else { camLat = cfg.CamLat; camLon = subLon + cfg.Phase; }
            double cla = camLat * Math.PI / 180, clo = camLon * Math.PI / 180;
            double dist = 5.0;
            double Ox = dist * Math.Cos(cla) * Math.Cos(clo), Oy = dist * Math.Cos(cla) * Math.Sin(clo), Oz = dist * Math.Sin(cla);
            double Fx = -Ox / dist, Fy = -Oy / dist, Fz = -Oz / dist;
            double Ux = -Fz * Fx, Uy = -Fz * Fy, Uz = 1 - Fz * Fz;     // north projected off the view axis
            double ul = Math.Sqrt(Ux * Ux + Uy * Uy + Uz * Uz); Ux /= ul; Uy /= ul; Uz /= ul;
            double Rx = Fy * Uz - Fz * Uy, Ry = Fz * Ux - Fx * Uz, Rz = Fx * Uy - Fy * Ux;

            double Dpx = cfg.Globe * H;
            double cx = cfg.CenterX * W, cy = cfg.CenterY * H;
            double alpha = Math.Asin(1 / dist);
            double focal = (Dpx / 2) / Math.Tan(alpha);
            double OO = dist * dist;
            double pw = Math.Sqrt(OO - 1) / focal;                 // world size of one pixel at the limb
            const double haloMax = 0.075, Hs = 0.0115;
            int Rh = (int)(Dpx / 2 * (1 + haloMax * 1.6)) + 4;

            int y0 = Math.Max(0, (int)(cy - Rh)), y1 = Math.Min(H - 1, (int)(cy + Rh));
            int x0 = Math.Max(0, (int)(cx - Rh)), x1 = Math.Min(W - 1, (int)(cx + Rh));
            double rh2 = (double)Rh * Rh;

            byte[] day = Day.D, night = Night.D, cloud = (cfg.Clouds && Cloud != null) ? Cloud.D : null, spec = Spec.D;
            const double TWO_PI = Math.PI * 2;
            float[] L = lin;

            for (int py = y0; py <= y1; py++)
            {
                double dy = cy - (py + 0.5);
                int rowOff = py * stride;
                for (int px = x0; px <= x1; px++)
                {
                    double dx = px + 0.5 - cx;
                    if (dx * dx + dy * dy > rh2) continue;
                    double rx = Fx * focal + Rx * dx + Ux * dy, ry = Fy * focal + Ry * dx + Uy * dy, rz = Fz * focal + Rz * dx + Uz * dy;
                    double rl = 1 / Math.Sqrt(rx * rx + ry * ry + rz * rz); rx *= rl; ry *= rl; rz *= rl;
                    double b = Ox * rx + Oy * ry + Oz * rz;
                    double c2 = OO - b * b; if (c2 < 0) c2 = 0;
                    double dc = Math.Sqrt(c2);
                    if (b > 0) continue;
                    double hgt = dc - 1;
                    if (hgt > haloMax) continue;

                    int o = rowOff + px * 3;
                    double bgR = L[buf[o + 2]], bgG = L[buf[o + 1]], bgB = L[buf[o]];
                    double cov = (1 - dc) / pw + 0.5; cov = cov < 0 ? 0 : cov > 1 ? 1 : cov;

                    // closest-approach point lighting (used for the halo and as the limb normal)
                    double qx = Ox - b * rx, qy = Oy - b * ry, qz = Oz - b * rz;
                    double ql = 1 / Math.Max(1e-9, Math.Sqrt(qx * qx + qy * qy + qz * qz));
                    double lq = (qx * Lx + qy * Ly + qz * Lz) * ql;

                    double fr = bgR, fg = bgG, fb = bgB;
                    if (hgt > -0.02)
                    {
                        // atmosphere halo: optical depth through an exponential shell, lit from the sun side
                        double hh = hgt < 0 ? 0 : hgt;
                        double dens = Math.Exp(-hh / Hs);
                        double lit = SS(-0.28, 0.45, lq);
                        double blue = SS(-0.06, 0.38, lq);
                        double hr = Mix(1.00, 0.30, blue), hg = Mix(0.42, 0.56, blue), hb = Mix(0.16, 1.00, blue);
                        double k = dens * lit * 0.62 + Math.Pow(dens, 6) * lit * 0.35;
                        double glow = Math.Exp(-hh / 0.035) * lit * 0.045;       // faint wide glow
                        fr += hr * k + 0.35 * glow; fg += hg * k + 0.6 * glow; fb += hb * k + glow;
                    }

                    if (cov > 0)
                    {
                        double disc = 1 - c2; if (disc < 0) disc = 0;
                        double t = -b - Math.Sqrt(disc);
                        double nx = Ox + t * rx, ny = Oy + t * ry, nz = Oz + t * rz;
                        double nl = 1 / Math.Sqrt(nx * nx + ny * ny + nz * nz); nx *= nl; ny *= nl; nz *= nl;
                        double NdL = nx * Lx + ny * Ly + nz * Lz;
                        double mu = -(nx * rx + ny * ry + nz * rz); if (mu < 0) mu = 0;

                        // texture lookup (bilinear, shared by all layers)
                        double u = (Math.Atan2(ny, nx) + Math.PI) / TWO_PI * TW - 0.5;
                        double v = (Math.PI / 2 - Math.Asin(nz > 1 ? 1 : nz < -1 ? -1 : nz)) / Math.PI * TH - 0.5;
                        int ix = (int)Math.Floor(u), iy = (int)Math.Floor(v);
                        double ffx = u - ix, ffy = v - iy;
                        int ixa = ((ix % TW) + TW) % TW, ixb = (ixa + 1) % TW;
                        int iya = iy < 0 ? 0 : iy >= TH ? TH - 1 : iy, iyb = iy + 1 < 0 ? 0 : iy + 1 >= TH ? TH - 1 : iy + 1;
                        int i00 = iya * TW + ixa, i10 = iya * TW + ixb, i01 = iyb * TW + ixa, i11 = iyb * TW + ixb;
                        double w00 = (1 - ffx) * (1 - ffy), w10 = ffx * (1 - ffy), w01 = (1 - ffx) * ffy, w11 = ffx * ffy;

                        double ar = L[day[i00 * 3]] * w00 + L[day[i10 * 3]] * w10 + L[day[i01 * 3]] * w01 + L[day[i11 * 3]] * w11;
                        double ag2 = L[day[i00 * 3 + 1]] * w00 + L[day[i10 * 3 + 1]] * w10 + L[day[i01 * 3 + 1]] * w01 + L[day[i11 * 3 + 1]] * w11;
                        double ab = L[day[i00 * 3 + 2]] * w00 + L[day[i10 * 3 + 2]] * w10 + L[day[i01 * 3 + 2]] * w01 + L[day[i11 * 3 + 2]] * w11;
                        double water = (spec[i00] * w00 + spec[i10] * w10 + spec[i01] * w01 + spec[i11] * w11) / 255.0;
                        double ca = 0;
                        if (cloud != null)
                        {
                            double cr0 = (cloud[i00] * w00 + cloud[i10] * w10 + cloud[i01] * w01 + cloud[i11] * w11) / 255.0;
                            ca = CloudAlpha(cr0, nz);
                        }

                        // sunlight reddens as it grazes the terminator
                        double warm = SS(-0.03, 0.17, NdL);
                        double sr = Mix(1.00, 1.0, warm), sg = Mix(0.66, 0.98, warm), sb = Mix(0.42, 0.95, warm);
                        const double sunI = 1.18;
                        double lam = NdL > 0 ? NdL : 0;

                        // cloud shadow: sample the cloud layer toward the sun
                        double shadow = 0;
                        if (cloud != null && NdL > 0)
                        {
                            double px2 = nx + Lx * 0.0045, py2 = ny + Ly * 0.0045, pz2 = nz + Lz * 0.0045;
                            double pl = 1 / Math.Sqrt(px2 * px2 + py2 * py2 + pz2 * pz2);
                            double su = (Math.Atan2(py2, px2) + Math.PI) / TWO_PI * TW;
                            double sv = (Math.PI / 2 - Math.Asin(Math.Max(-1, Math.Min(1, pz2 * pl)))) / Math.PI * TH;
                            int sx = (((int)su % TW) + TW) % TW, sy = Math.Max(0, Math.Min(TH - 1, (int)sv));
                            shadow = CloudAlpha(cloud[sy * TW + sx] / 255.0, pz2 * pl);
                        }

                        // oceans a touch deeper, land as photographed
                        double od = 1 - 0.30 * water;
                        ar *= od; ag2 *= od; ab *= od;
                        // gentle saturation lift so land reads rich, like an orbital photo
                        double al = ar * 0.25 + ag2 * 0.6 + ab * 0.15;
                        ar = Math.Max(0, al + (ar - al) * 1.25); ag2 = Math.Max(0, al + (ag2 - al) * 1.25); ab = Math.Max(0, al + (ab - al) * 1.25);
                        double gl = lam * sunI * (1 - 0.6 * shadow);
                        double r = ar * gl * sr, g = ag2 * gl * sg, bb = ab * gl * sb;

                        // sun glint on water
                        if (water > 0.02 && NdL > 0)
                        {
                            double vx = -rx, vy = -ry, vz = -rz;
                            double hx = Lx + vx, hy = Ly + vy, hz = Lz + vz;
                            double hl = 1 / Math.Sqrt(hx * hx + hy * hy + hz * hz);
                            double ndh = (nx * hx + ny * hy + nz * hz) * hl; if (ndh < 0) ndh = 0;
                            double fres = 0.25 + 0.75 * Math.Pow(1 - mu, 5);
                            double sp = (Math.Pow(ndh, 160) * 2.2 + Math.Pow(ndh, 18) * 0.10) * fres * water * (1 - ca) * (1 - 0.6 * shadow) * SS(0.0, 0.25, NdL);
                            r += sp * sr; g += sp * sg; bb += sp * sb;
                        }

                        // clouds
                        double cl = (NdL + 0.06) / 1.06; cl = cl < 0 ? 0 : cl > 1 ? 1 : cl;
                        cl = Math.Pow(cl, 0.85) * sunI * 0.86;
                        r = Mix(r, cl * sr, ca); g = Mix(g, cl * sg, ca); bb = Mix(bb, cl * sb, ca);

                        // night side: city lights + faint skyglow so the dark hemisphere still has shape
                        double nightW = SS(0.06, -0.16, NdL);
                        if (nightW > 0)
                        {
                            double lr = L[night[i00 * 3]] * w00 + L[night[i10 * 3]] * w10 + L[night[i01 * 3]] * w01 + L[night[i11 * 3]] * w11;
                            double lg = L[night[i00 * 3 + 1]] * w00 + L[night[i10 * 3 + 1]] * w10 + L[night[i01 * 3 + 1]] * w01 + L[night[i11 * 3 + 1]] * w11;
                            double lb = L[night[i00 * 3 + 2]] * w00 + L[night[i10 * 3 + 2]] * w10 + L[night[i01 * 3 + 2]] * w01 + L[night[i11 * 3 + 2]] * w11;
                            double lum = (lr * 0.3 + lg * 0.6 + lb * 0.1);
                            double city = (SS(0.02, 0.30, lum) * 0.9 + Math.Max(0, lum - 0.016) * 1.8) * (1 - 0.7 * ca) * nightW;
                            double amb = nightW * 0.022;
                            r += city * 1.00 + (ar * 0.35 + ca * 0.30) * amb * 0.55;
                            g += city * 0.64 + (ag2 * 0.35 + ca * 0.30) * amb * 0.75;
                            bb += city * 0.28 + (ab * 0.35 + ca * 0.30) * amb * 1.30;
                        }

                        // aerial perspective - the blue veil thickening toward the limb
                        double aer = 1 - Math.Exp(-0.045 / (mu + 0.055));
                        double hl2 = SS(-0.14, 0.35, NdL);
                        double blue2 = SS(-0.10, 0.10, NdL);
                        double ahr = Mix(0.80, 0.24, blue2), ahg = Mix(0.42, 0.48, blue2), ahb = Mix(0.30, 1.00, blue2);
                        double keep = 1 - aer * 0.55 * hl2 - aer * 0.25 * (1 - hl2);
                        r = r * keep + ahr * aer * hl2 * 0.85;
                        g = g * keep + ahg * aer * hl2 * 0.85;
                        bb = bb * keep + ahb * aer * hl2 * 0.85;

                        fr = fr * (1 - cov) + r * cov; fg = fg * (1 - cov) + g * cov; fb = fb * (1 - cov) + bb * cov;
                    }

                    buf[o + 2] = Out(fr); buf[o + 1] = Out(fg); buf[o] = Out(fb);
                }
            }

            var bmp = new Bitmap(W, H, PixelFormat.Format24bppRgb);
            var bd = bmp.LockBits(new Rectangle(0, 0, W, H), ImageLockMode.WriteOnly, PixelFormat.Format24bppRgb);
            if (bd.Stride == stride) Marshal.Copy(buf, 0, bd.Scan0, buf.Length);
            else for (int y = 0; y < H; y++) Marshal.Copy(buf, y * stride, new IntPtr(bd.Scan0.ToInt64() + (long)y * bd.Stride), W * 3);
            bmp.UnlockBits(bd);

            var f = new Frame { Image = bmp, SubLat = decl * 180 / Math.PI, SubLon = subLon };
            // home marker
            double hla = cfg.Lat * Math.PI / 180, hlo = cfg.Lon * Math.PI / 180;
            double Hx = Math.Cos(hla) * Math.Cos(hlo), Hy = Math.Cos(hla) * Math.Sin(hlo), Hz = Math.Sin(hla);
            f.HomeSunAlt = Math.Asin(Math.Max(-1, Math.Min(1, Hx * Lx + Hy * Ly + Hz * Lz))) * 180 / Math.PI;
            double vx2 = Hx - Ox, vy2 = Hy - Oy, vz2 = Hz - Oz;
            double facing = -(vx2 * Hx + vy2 * Hy + vz2 * Hz) / Math.Sqrt(vx2 * vx2 + vy2 * vy2 + vz2 * vz2);
            if (facing > 0)
            {
                double zf = vx2 * Fx + vy2 * Fy + vz2 * Fz;
                f.MarkerX = cx + (vx2 * Rx + vy2 * Ry + vz2 * Rz) / zf * focal;
                f.MarkerY = cy - (vx2 * Ux + vy2 * Uy + vz2 * Uz) / zf * focal;
                f.MarkerAlpha = SS(0.05, 0.3, facing);
            }
            f.Ms = sw.ElapsedMilliseconds;
            return f;
        }
    }

    class App : ApplicationContext
    {
        public static string AppDir, TexDir, DataDir;
        readonly Config cfg;
        readonly Renderer rnd = new Renderer();
        readonly NotifyIcon tray;
        readonly AutoResetEvent wake = new AutoResetEvent(false);
        readonly Thread worker;
        volatile bool running = true, paused, locked, forceNext = true;
        int loadedMonth = -1; DateTime cloudStamp = DateTime.MinValue, lastCloudTry = DateTime.MinValue;
        PerformanceCounter cpu;
        int skips;
        bool frameToggle;
        ToolStripMenuItem miSun, miHome, miPause, miClouds, miText, miStart;

        const string NightUrl = "https://eoimages.gsfc.nasa.gov/images/imagerecords/144000/144898/BlackMarble_2016_01deg.jpg";
        const string CloudUrl = "https://clouds.matteason.co.uk/images/4096x2048/clouds.jpg";
        const string SpecUrl = "https://clouds.matteason.co.uk/images/4096x2048/specular.jpg";
        static readonly int[] DayIds = { 73580, 73605, 73630, 73655, 73701, 73726, 73751, 73776, 73801, 73826, 73884, 73909 };
        static string DayUrl(int month) { return string.Format("https://eoimages.gsfc.nasa.gov/images/imagerecords/73000/{0}/world.topo.bathy.2004{1:D2}.3x5400x2700.jpg", DayIds[month - 1], month); }

        public App(Config c)
        {
            cfg = c;
            BackupOriginalWallpaper();
            tray = new NotifyIcon { Icon = MakeIcon(), Text = "Live Earth", Visible = true, ContextMenuStrip = BuildMenu() };
            tray.DoubleClick += delegate { Kick(); };
            SystemEvents.SessionSwitch += OnSessionSwitch;
            SystemEvents.PowerModeChanged += OnPower;
            SystemEvents.DisplaySettingsChanged += delegate { Kick(); };
            worker = new Thread(Loop) { IsBackground = true, Priority = ThreadPriority.Lowest, Name = "render" };
            worker.Start();
            if (!cfg.HasPlace)
                tray.ShowBalloonTip(8000, "Live Earth is running",
                    "Your wallpaper updates every minute. Right-click the globe icon to add your city (Settings file) or turn on Start with Windows.", ToolTipIcon.Info);
        }

        void Kick() { forceNext = true; wake.Set(); }

        ContextMenuStrip BuildMenu()
        {
            var m = new ContextMenuStrip();
            var title = new ToolStripMenuItem("Live Earth") { Enabled = false };
            m.Items.Add(title);
            m.Items.Add(new ToolStripSeparator());
            m.Items.Add("Refresh now", null, delegate { Kick(); });
            miSun = new ToolStripMenuItem("View: Earth spinning under the Sun", null, delegate { cfg.Mode = "sun"; cfg.Save(); SyncMenu(); Kick(); });
            miHome = new ToolStripMenuItem(cfg.HasPlace ? "View: Parked above " + cfg.Place : "View: Parked above my city (set it in Settings)", null, delegate { cfg.Mode = "home"; cfg.Save(); SyncMenu(); Kick(); });
            m.Items.Add(miSun); m.Items.Add(miHome);
            miClouds = new ToolStripMenuItem("Live clouds", null, delegate { cfg.Clouds = !cfg.Clouds; cfg.Save(); SyncMenu(); Kick(); });
            miText = new ToolStripMenuItem("Clock && info text", null, delegate { cfg.ShowText = !cfg.ShowText; cfg.Save(); SyncMenu(); Kick(); });
            m.Items.Add(miClouds); m.Items.Add(miText);
            miPause = new ToolStripMenuItem("Pause", null, delegate { paused = !paused; SyncMenu(); if (!paused) Kick(); });
            m.Items.Add(miPause);
            miStart = new ToolStripMenuItem("Start with Windows", null, delegate { SetAutostart(!IsAutostart()); SyncMenu(); });
            m.Items.Add(miStart);
            m.Items.Add(new ToolStripSeparator());
            m.Items.Add("Settings file...", null, delegate { try { Process.Start("notepad.exe", "\"" + cfg.FilePath + "\""); } catch { } });
            m.Items.Add("Restore my old wallpaper && exit", null, delegate { RestoreWallpaper(); Quit(); });
            m.Items.Add("Exit", null, delegate { Quit(); });
            SyncMenu();
            return m;
        }

        void SyncMenu()
        {
            miSun.Checked = cfg.Mode != "home"; miHome.Checked = cfg.Mode == "home";
            miClouds.Checked = cfg.Clouds; miText.Checked = cfg.ShowText; miPause.Checked = paused;
            miStart.Checked = IsAutostart();
        }

        const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
        static string StartupLink { get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Startup), "Live Earth.lnk"); } }

        static bool IsAutostart()
        {
            if (File.Exists(StartupLink)) return true;
            try { using (var k = Registry.CurrentUser.OpenSubKey(RunKey)) return k != null && k.GetValue("LiveEarth") != null; } catch { return false; }
        }

        static void SetAutostart(bool on)
        {
            try
            {
                using (var k = Registry.CurrentUser.CreateSubKey(RunKey))
                {
                    if (on) k.SetValue("LiveEarth", "\"" + Application.ExecutablePath + "\"");
                    else if (k.GetValue("LiveEarth") != null) k.DeleteValue("LiveEarth");
                }
                if (!on && File.Exists(StartupLink)) File.Delete(StartupLink);
            }
            catch (Exception ex) { Log.W("autostart: " + ex.Message); }
        }

        void Quit()
        {
            running = false; wake.Set();
            tray.Visible = false; tray.Dispose();
            SystemEvents.SessionSwitch -= OnSessionSwitch; SystemEvents.PowerModeChanged -= OnPower;
            ExitThread();
        }

        void OnSessionSwitch(object s, SessionSwitchEventArgs e)
        {
            if (e.Reason == SessionSwitchReason.SessionLock) locked = true;
            else if (e.Reason == SessionSwitchReason.SessionUnlock) { locked = false; Kick(); }
        }
        void OnPower(object s, PowerModeChangedEventArgs e) { if (e.Mode == PowerModes.Resume) Kick(); }

        static Icon MakeIcon()
        {
            using (var bmp = new Bitmap(32, 32))
            {
                using (var g = Graphics.FromImage(bmp))
                {
                    g.SmoothingMode = SmoothingMode.AntiAlias;
                    using (var br = new LinearGradientBrush(new Rectangle(2, 2, 28, 28), Color.FromArgb(120, 200, 255), Color.FromArgb(10, 30, 70), 20f))
                        g.FillEllipse(br, 3, 3, 26, 26);
                    using (var p = new Pen(Color.FromArgb(200, 140, 210, 255), 1.6f)) g.DrawEllipse(p, 2.5f, 2.5f, 27, 27);
                }
                IntPtr h = bmp.GetHicon();
                var ic = (Icon)Icon.FromHandle(h).Clone();
                Native.DestroyIcon(h);
                return ic;
            }
        }

        // ---------- wallpaper plumbing ----------
        static string FrameDir { get { return Path.Combine(DataDir, "frames"); } }

        static void BackupOriginalWallpaper()
        {
            try
            {
                using (var k = Registry.CurrentUser.OpenSubKey(@"Control Panel\Desktop"))
                {
                    var cur = k == null ? null : k.GetValue("WallPaper") as string;
                    if (!string.IsNullOrEmpty(cur) && cur.IndexOf("LiveEarth", StringComparison.OrdinalIgnoreCase) < 0)
                        File.WriteAllText(Path.Combine(DataDir, "original_wallpaper.txt"), cur);
                }
            }
            catch (Exception ex) { Log.W("backup: " + ex.Message); }
        }

        public static void RestoreWallpaper()
        {
            try
            {
                var f = Path.Combine(DataDir, "original_wallpaper.txt");
                if (File.Exists(f))
                {
                    var p = File.ReadAllText(f).Trim();
                    Native.SystemParametersInfo(Native.SPI_SETDESKWALLPAPER, 0, p, Native.SPIF_UPDATEINIFILE | Native.SPIF_SENDCHANGE);
                }
            }
            catch (Exception ex) { Log.W("restore: " + ex.Message); }
        }

        void SetWallpaper(Bitmap bmp)
        {
            Directory.CreateDirectory(FrameDir);
            frameToggle = !frameToggle;
            string path = Path.Combine(FrameDir, frameToggle ? "earth_a.png" : "earth_b.png");
            bmp.Save(path, ImageFormat.Png);
            if (!Native.SystemParametersInfo(Native.SPI_SETDESKWALLPAPER, 0, path, Native.SPIF_UPDATEINIFILE))
                Log.W("SystemParametersInfo failed: " + Marshal.GetLastWin32Error());
        }

        // ---------- assets ----------
        static bool Fetch(string url, string dest)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(dest));
                string tmp = dest + ".part";
                using (var wc = new WebClient())
                {
                    wc.Headers[HttpRequestHeader.UserAgent] = "LiveEarthWallpaper/1.0";
                    wc.DownloadFile(url, tmp);
                }
                using (var ms = new MemoryStream(File.ReadAllBytes(tmp))) using (var test = new Bitmap(ms)) { }   // must decode
                if (File.Exists(dest)) File.Delete(dest);
                File.Move(tmp, dest);
                Log.W("downloaded " + url);
                return true;
            }
            catch (Exception ex) { Log.W("download failed " + url + ": " + ex.Message); try { File.Delete(dest + ".part"); } catch { } return false; }
        }

        public static void EnsureStatic(Renderer r)
        {
            string nightPath = Path.Combine(TexDir, "night_blackmarble2016.jpg");
            string specPath = Path.Combine(TexDir, "water_mask.jpg");
            if (!File.Exists(nightPath)) Fetch(NightUrl, nightPath);
            if (!File.Exists(specPath)) Fetch(SpecUrl, specPath);
            if (r.Night == null && File.Exists(nightPath)) r.Night = Tex.Load(nightPath, Renderer.TW, Renderer.TH, 3);
            if (r.Spec == null && File.Exists(specPath)) r.Spec = Tex.Load(specPath, Renderer.TW, Renderer.TH, 1);
        }

        // ---------- live night lights (last night's VIIRS Day/Night Band passes from NASA GIBS) ----------
        static DateTime lightsStamp = DateTime.MinValue, lightsTry = DateTime.MinValue;
        static bool lightsLive;
        public static string LightsLabel = "";

        public static void EnsureLights(Renderer r, Config cfg)
        {
            string bmPath = Path.Combine(TexDir, "night_blackmarble2016.jpg");
            string livePath = Path.Combine(TexDir, "lights_live.png");
            if (!cfg.LiveLights)
            {
                if (lightsLive || r.Night == null) { r.Night = Tex.Load(bmPath, Renderer.TW, Renderer.TH, 3); lightsLive = false; LightsLabel = ""; }
                return;
            }
            bool stale = !File.Exists(livePath) || (DateTime.UtcNow - File.GetLastWriteTimeUtc(livePath)).TotalHours > 8;
            if (stale && (DateTime.UtcNow - lightsTry).TotalMinutes > 60)
            {
                lightsTry = DateTime.UtcNow;
                try { BuildLiveLights(bmPath, livePath); } catch (Exception ex) { Log.W("live lights: " + ex.Message); }
            }
            if (File.Exists(livePath))
            {
                var ts = File.GetLastWriteTimeUtc(livePath);
                if (!lightsLive || ts != lightsStamp)
                {
                    r.Night = Tex.Load(livePath, Renderer.TW, Renderer.TH, 3);
                    lightsStamp = ts; lightsLive = true; GC.Collect();
                    try { LightsLabel = File.ReadAllText(livePath + ".txt").Trim(); } catch { LightsLabel = ""; }
                }
            }
        }

        static void BuildLiveLights(string bmPath, string outPath)
        {
            const int W = 4096, H = 2048;
            string[] layers = { "VIIRS_SNPP_DayNightBand_At_Sensor_Radiance", "VIIRS_NOAA20_DayNightBand_At_Sensor_Radiance", "VIIRS_NOAA21_DayNightBand_At_Sensor_Radiance" };
            var acc = new float[W * H]; var have = new bool[W * H];
            int got = 0; DateTime night = DateTime.MinValue;
            string tmp = Path.Combine(TexDir, "viirs.tmp.jpg");
            for (int back = 1; back <= 3 && got == 0; back++)
            {
                var day = DateTime.UtcNow.Date.AddDays(-back);
                foreach (var layer in layers)
                {
                    string url = "https://gibs.earthdata.nasa.gov/wms/epsg4326/best/wms.cgi?SERVICE=WMS&REQUEST=GetMap&VERSION=1.3.0&LAYERS=" + layer +
                        "&CRS=EPSG:4326&BBOX=-90,-180,90,180&WIDTH=" + W + "&HEIGHT=" + H + "&FORMAT=image/jpeg&TIME=" + day.ToString("yyyy-MM-dd");
                    if (!Fetch(url, tmp)) continue;
                    if (new FileInfo(tmp).Length < 60000) continue;      // empty layer for that date
                    var t = Tex.Load(tmp, W, H, 1);
                    for (int i = 0; i < t.D.Length; i++) { int v = t.D[i]; if (v > 4) { have[i] = true; float f = v / 255f; if (f > acc[i]) acc[i] = f; } }
                    got++; night = day;
                }
            }
            try { File.Delete(tmp); } catch { }
            if (got == 0) { Log.W("live lights: no VIIRS data available"); return; }

            // high-pass: lights are point sources, moonlit clouds / airglow / twilight are smooth
            const int R = 10;
            var integ = new double[(W + 1) * (H + 1)];
            for (int y = 0; y < H; y++)
            {
                double row = 0;
                for (int x = 0; x < W; x++) { row += acc[y * W + x]; integ[(y + 1) * (W + 1) + x + 1] = integ[y * (W + 1) + x + 1] + row; }
            }
            var bm = Tex.Load(bmPath, W, H, 3);
            var hp = new float[W * H];
            var vals = new List<float>(); var bmVals = new List<float>();
            for (int y = 0; y < H; y++)
            {
                int y0 = Math.Max(0, y - R), y1 = Math.Min(H, y + R + 1);
                for (int x = 0; x < W; x++)
                {
                    int i = y * W + x;
                    int x0 = Math.Max(0, x - R), x1 = Math.Min(W, x + R + 1);
                    double sum = integ[y1 * (W + 1) + x1] - integ[y0 * (W + 1) + x1] - integ[y1 * (W + 1) + x0] + integ[y0 * (W + 1) + x0];
                    double mean = sum / ((y1 - y0) * (x1 - x0));
                    // no pass, or a sunlit / twilight swath (only happens at high latitudes; bright megacities must not trip it)
                    double latDeg = 90 - (y + 0.5) * 180.0 / H;
                    if (!have[i] || (mean > 0.33 && Math.Abs(latDeg) > 48)) { have[i] = false; continue; }
                    float v = (float)Math.Max(0, acc[i] - mean * 1.05 - 0.025);
                    hp[i] = v;
                    float b = (bm.D[i * 3] * 0.3f + bm.D[i * 3 + 1] * 0.6f + bm.D[i * 3 + 2] * 0.1f) / 255f;
                    if (v > 0 && (i & 7) == 0) vals.Add(v);
                    if (b > 0.12f && (i & 7) == 0) bmVals.Add(b);
                }
            }
            if (vals.Count < 1000) { Log.W("live lights: too little signal"); return; }
            vals.Sort(); bmVals.Sort();
            float scale = bmVals[(int)(bmVals.Count * 0.985)] / Math.Max(1e-3f, vals[(int)(vals.Count * 0.985)]);

            // where lights could plausibly be (2016 lights, blurred wide) - rejects ice / cloud-edge noise but lets
            // last night's reality decide what is actually lit
            var plaus = new double[(W + 1) * (H + 1)];
            for (int y = 0; y < H; y++)
            {
                double row = 0;
                for (int x = 0; x < W; x++)
                {
                    int i = y * W + x;
                    row += (bm.D[i * 3 + 1] / 255.0) > 0.10 ? 1 : 0;
                    plaus[(y + 1) * (W + 1) + x + 1] = plaus[y * (W + 1) + x + 1] + row;
                }
            }
            const int P = 6;
            using (var outBmp = new Bitmap(W, H, PixelFormat.Format24bppRgb))
            {
                var bd = outBmp.LockBits(new Rectangle(0, 0, W, H), ImageLockMode.WriteOnly, PixelFormat.Format24bppRgb);
                var line = new byte[bd.Stride];
                for (int y = 0; y < H; y++)
                {
                    int y0 = Math.Max(0, y - P), y1 = Math.Min(H, y + P + 1);
                    for (int x = 0; x < W; x++)
                    {
                        int i = y * W + x;
                        byte r, g, b;
                        if (have[i])
                        {
                            int x0 = Math.Max(0, x - P), x1 = Math.Min(W, x + P + 1);
                            double near = plaus[y1 * (W + 1) + x1] - plaus[y0 * (W + 1) + x1] - plaus[y1 * (W + 1) + x0] + plaus[y0 * (W + 1) + x0];
                            float v = near > 0 ? (float)Math.Pow(Math.Min(1f, hp[i] * scale), 0.8) : 0f;
                            r = g = b = (byte)(v * 255);
                        }
                        else { r = bm.D[i * 3]; g = bm.D[i * 3 + 1]; b = bm.D[i * 3 + 2]; }   // gap in last night's passes
                        line[x * 3] = b; line[x * 3 + 1] = g; line[x * 3 + 2] = r;
                    }
                    Marshal.Copy(line, 0, new IntPtr(bd.Scan0.ToInt64() + (long)y * bd.Stride), bd.Stride);
                }
                outBmp.UnlockBits(bd);
                outBmp.Save(outPath + ".part", ImageFormat.Png);
            }
            if (File.Exists(outPath)) File.Delete(outPath);
            File.Move(outPath + ".part", outPath);
            File.WriteAllText(outPath + ".txt", "Lights  night of " + night.ToString("d MMM", CultureInfo.InvariantCulture) + " · VIIRS");
            Log.W("live lights built from " + got + " satellite passes, night of " + night.ToString("yyyy-MM-dd"));
        }

        public static bool EnsureDay(Renderer r, int month, ref int loaded)
        {
            if (loaded == month && r.Day != null) return true;
            string p = Path.Combine(TexDir, string.Format("day_{0:D2}.jpg", month));
            if (!File.Exists(p)) Fetch(DayUrl(month), p);
            if (!File.Exists(p))
            {   // fall back to any month we already have
                var any = Directory.Exists(TexDir) ? Directory.GetFiles(TexDir, "day_*.jpg") : new string[0];
                if (any.Length == 0) return false;
                if (r.Day != null) return true;
                p = any[0];
            }
            r.Day = Tex.Load(p, Renderer.TW, Renderer.TH, 3);
            loaded = month;
            return true;
        }

        public static void EnsureClouds(Renderer r, ref DateTime stamp, ref DateTime lastTry)
        {
            string p = Path.Combine(TexDir, "clouds_live.jpg");
            bool stale = !File.Exists(p) || (DateTime.UtcNow - File.GetLastWriteTimeUtc(p)).TotalHours > 3;
            if (stale && (DateTime.UtcNow - lastTry).TotalMinutes > 30)
            {
                lastTry = DateTime.UtcNow;
                Fetch(CloudUrl, p);
            }
            if (File.Exists(p))
            {
                var ts = File.GetLastWriteTimeUtc(p);
                if (r.Cloud == null || ts != stamp) { r.Cloud = Tex.Load(p, Renderer.TW, Renderer.TH, 1); stamp = ts; GC.Collect(); }
            }
        }

        // ---------- loop ----------
        void Loop()
        {
            try { cpu = new PerformanceCounter("Processor", "% Processor Time", "_Total"); cpu.NextValue(); } catch { cpu = null; }
            while (running)
            {
                bool force = forceNext; forceNext = false;
                if (!force)
                {
                    var now = DateTime.Now;
                    int iv = cfg.Interval;
                    double into = (now.TimeOfDay.TotalSeconds % iv);
                    int waitMs = (int)((iv - into) * 1000) + 150;
                    wake.WaitOne(waitMs);
                    if (!running) break;
                    force = forceNext; forceNext = false;
                }
                if (paused && !force) continue;
                if (!force && ShouldSkip()) continue;
                try { Tick(); }
                catch (Exception ex) { Log.W("tick: " + ex); }
            }
        }

        bool ShouldSkip()
        {
            if (locked) return true;
            try
            {
                int st; if (Native.SHQueryUserNotificationState(out st) == 0 && (st == 2 || st == 3 || st == 4)) return true; // fullscreen game/video/presentation
            }
            catch { }
            if (cpu != null && skips < 5)
            {
                float load = 0; try { load = cpu.NextValue(); } catch { }
                if (load > cfg.CpuLimit) { skips++; Log.W("busy (" + (int)load + "% CPU) - skipping frame"); return true; }
            }
            skips = 0;
            return false;
        }

        void Tick()
        {
            DateTime utc = DateTime.UtcNow;
            EnsureStatic(rnd);
            if (!EnsureDay(rnd, DateTime.Now.Month, ref loadedMonth) || rnd.Night == null || rnd.Spec == null)
            {
                Log.W("textures not ready (offline?) - retrying next minute");
                return;
            }
            if (cfg.Clouds) EnsureClouds(rnd, ref cloudStamp, ref lastCloudTry);
            EnsureLights(rnd, cfg);
            var screen = Screen.PrimaryScreen.Bounds;
            var work = Screen.PrimaryScreen.WorkingArea;
            var fr = rnd.Render(utc, cfg, screen);
            using (fr.Image)
            {
                Overlay.Draw(fr, cfg, DateTime.Now, work, cloudStamp, rnd.Cloud != null && cfg.Clouds);
                SetWallpaper(fr.Image);
            }
            Log.W("frame " + fr.Ms + " ms");
        }

        public static Renderer.Frame RenderOnce(Config cfg, Renderer r, DateTime utc, Size size, string outPath)
        {
            int lm = -1; DateTime cs = DateTime.MinValue, lt = DateTime.MinValue;
            EnsureStatic(r);
            EnsureDay(r, utc.ToLocalTime().Month, ref lm);
            if (cfg.Clouds) EnsureClouds(r, ref cs, ref lt);
            EnsureLights(r, cfg);
            var fr = r.Render(utc, cfg, new Rectangle(0, 0, size.Width, size.Height));
            var work = new Rectangle(0, 0, size.Width, size.Height - (int)(size.Height * 0.0667));
            Overlay.Draw(fr, cfg, utc.ToLocalTime(), work, cs, r.Cloud != null && cfg.Clouds);
            fr.Image.Save(outPath, ImageFormat.Png);
            return fr;
        }
    }

    static class Overlay
    {
        static FontFamily Pick(params string[] names)
        {
            foreach (var n in names) { try { return new FontFamily(n); } catch { } }
            return FontFamily.GenericSansSerif;
        }

        static string Deg(double v, char pos, char neg) { return Math.Abs(v).ToString("0.0", CultureInfo.InvariantCulture) + "°" + (v >= 0 ? pos : neg); }

        public static void Draw(Renderer.Frame fr, Config cfg, DateTime local, Rectangle work, DateTime cloudStampUtc, bool clouds)
        {
            var bmp = fr.Image;
            double s = bmp.Height / 1080.0;
            using (var g = Graphics.FromImage(bmp))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
                g.InterpolationMode = InterpolationMode.HighQualityBicubic;

                if (cfg.ShowMarker && cfg.HasPlace && fr.MarkerAlpha > 0.01)
                {
                    float mx = (float)fr.MarkerX, my = (float)fr.MarkerY; double a = fr.MarkerAlpha;
                    bool nightAtHome = fr.HomeSunAlt < -0.8;
                    Color core = nightAtHome ? Color.FromArgb(255, 214, 150) : Color.FromArgb(235, 250, 255);
                    float gr = (float)(16 * s);
                    using (var path = new GraphicsPath())
                    {
                        path.AddEllipse(mx - gr, my - gr, gr * 2, gr * 2);
                        using (var pb = new PathGradientBrush(path))
                        {
                            pb.CenterColor = Color.FromArgb((int)(110 * a), core);
                            pb.SurroundColors = new[] { Color.FromArgb(0, core) };
                            g.FillPath(pb, path);
                        }
                    }
                    float dr = (float)(2.6 * s), rr = (float)(7.5 * s);
                    using (var b = new SolidBrush(Color.FromArgb((int)(255 * a), core))) g.FillEllipse(b, mx - dr, my - dr, dr * 2, dr * 2);
                    using (var p = new Pen(Color.FromArgb((int)(170 * a), core), (float)(1.1 * s))) g.DrawEllipse(p, mx - rr, my - rr, rr * 2, rr * 2);
                    // leader + label
                    float lx = mx + (float)(14 * s), ly = my - (float)(14 * s), ex = lx + (float)(18 * s);
                    using (var p = new Pen(Color.FromArgb((int)(120 * a), 255, 255, 255), (float)(1.0 * s))) { g.DrawLine(p, mx + rr * 0.72f, my - rr * 0.72f, lx, ly); g.DrawLine(p, lx, ly, ex, ly); }
                    using (var f = new Font(Pick("Segoe UI Semibold", "Segoe UI"), (float)(12.5 * s), FontStyle.Regular, GraphicsUnit.Pixel))
                    using (var b = new SolidBrush(Color.FromArgb((int)(225 * a), 255, 255, 255)))
                        g.DrawString(cfg.Place.ToUpperInvariant(), f, b, ex + (float)(4 * s), ly - f.Height * 0.55f);
                }

                if (!cfg.ShowText) return;

                var light = Pick("Segoe UI Light", "Segoe UI");
                var semi = Pick("Segoe UI Semilight", "Segoe UI");
                var reg = Pick("Segoe UI");
                float right = (float)(bmp.Width - 70 * s);
                float y = (float)(work.Bottom - 46 * s);
                var fmt = new StringFormat(StringFormat.GenericTypographic) { Alignment = StringAlignment.Far };
                fmt.FormatFlags |= StringFormatFlags.NoClip;

                // sunrise / sunset for home
                DateTime rise, set;
                string sunLine;
                var tf = CultureInfo.CurrentCulture.DateTimeFormat.ShortTimePattern.Contains("tt") ? "h:mm tt" : "HH:mm";
                bool ok = Sun.RiseSet(local, cfg.Lat, cfg.Lon, out rise, out set);
                if (!cfg.HasPlace) sunLine = null;
                else if (!ok) sunLine = fr.HomeSunAlt > 0 ? "Midnight sun in " + cfg.Place : "Polar night in " + cfg.Place;
                else
                {
                    var nowU = local.ToUniversalTime();
                    if (fr.HomeSunAlt > -0.833) sunLine = "Daylight in " + cfg.Place + "   ·   sunset " + set.ToLocalTime().ToString(tf, CultureInfo.CurrentCulture).ToLowerInvariant();
                    else
                    {
                        DateTime r2 = rise, s2;
                        if (nowU > rise) Sun.RiseSet(local.AddDays(1), cfg.Lat, cfg.Lon, out r2, out s2);
                        string state = fr.HomeSunAlt > -6 ? "Twilight in " : fr.HomeSunAlt > -18 ? "Dusk sky over " : "Night in ";
                        if (nowU < rise && fr.HomeSunAlt > -18) state = fr.HomeSunAlt > -6 ? "Dawn in " : "First light over ";
                        sunLine = state + cfg.Place + "   ·   sunrise " + r2.ToLocalTime().ToString(tf, CultureInfo.CurrentCulture).ToLowerInvariant();
                    }
                }

                string meta = "Sun overhead  " + Deg(fr.SubLat, 'N', 'S') + "  " + Deg(fr.SubLon, 'E', 'W');
                if (clouds && cloudStampUtc > DateTime.MinValue)
                {
                    double hrs = (DateTime.UtcNow - cloudStampUtc).TotalHours;
                    meta += "     Clouds  " + (hrs < 1 ? "<1 h" : ((int)hrs) + " h") + " old · EUMETSAT";
                }
                if (App.LightsLabel.Length > 0) meta += "     " + App.LightsLabel;

                using (var fSmall = new Font(reg, (float)(12 * s), GraphicsUnit.Pixel))
                using (var fMid = new Font(semi, (float)(19 * s), GraphicsUnit.Pixel))
                using (var fDate = new Font(semi, (float)(21 * s), GraphicsUnit.Pixel))
                using (var fBig = new Font(light, (float)(78 * s), GraphicsUnit.Pixel))
                using (var fAmPm = new Font(light, (float)(24 * s), GraphicsUnit.Pixel))
                using (var dim = new SolidBrush(Color.FromArgb(120, 200, 220, 240)))
                using (var mid = new SolidBrush(Color.FromArgb(185, 225, 235, 248)))
                using (var hi = new SolidBrush(Color.FromArgb(240, 255, 255, 255)))
                {
                    y -= fSmall.Height; g.DrawString(meta, fSmall, dim, right, y, fmt);
                    if (sunLine != null) { y -= fMid.Height + (float)(10 * s); g.DrawString(sunLine, fMid, mid, right, y, fmt); y -= fDate.Height + (float)(4 * s); }
                    else y -= fDate.Height + (float)(10 * s);
                    g.DrawString(local.ToString("dddd, d MMMM", CultureInfo.CurrentCulture), fDate, hi, right, y, fmt);

                    bool twelve = tf.Contains("tt");
                    string big = local.ToString(twelve ? "h:mm" : "HH:mm", CultureInfo.InvariantCulture);
                    float ampmW = 0;
                    if (twelve)
                    {
                        string ap = local.Hour < 12 ? "AM" : "PM";
                        ampmW = g.MeasureString(ap, fAmPm, 1000, fmt).Width + (float)(8 * s);
                        g.DrawString(ap, fAmPm, mid, right, y - fAmPm.Height - (float)(12 * s), fmt);
                    }
                    y -= fBig.Height * 0.98f;
                    g.DrawString(big, fBig, hi, right - ampmW, y, fmt);

                    // LIVE tag
                    float ty = y - (float)(6 * s);
                    using (var fTag = new Font(reg, (float)(11.5 * s), FontStyle.Bold, GraphicsUnit.Pixel))
                    {
                        string tag = "L I V E   E A R T H";
                        var sz = g.MeasureString(tag, fTag, 1000, fmt);
                        g.DrawString(tag, fTag, dim, right, ty, fmt);
                        float dr = (float)(3.4 * s);
                        float dx = right - sz.Width - (float)(12 * s), dyy = ty + fTag.Height * 0.5f;
                        using (var b = new SolidBrush(Color.FromArgb(230, 90, 220, 160))) g.FillEllipse(b, dx - dr, dyy - dr, dr * 2, dr * 2);
                        using (var b = new SolidBrush(Color.FromArgb(60, 90, 220, 160))) g.FillEllipse(b, dx - dr * 2.4f, dyy - dr * 2.4f, dr * 4.8f, dr * 4.8f);
                    }
                }
            }
        }
    }

    static class Program
    {
        [STAThread]
        static void Main(string[] args)
        {
            Native.SetProcessDPIAware();
            ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls12;
            App.AppDir = AppDomain.CurrentDomain.BaseDirectory;
            App.TexDir = Path.Combine(App.AppDir, "textures");
            // Keep frames next to the exe: %LOCALAPPDATA% can be silently redirected (MSIX container) when we are
            // launched by a packaged app, and Explorer then can't see the wallpaper file -> black desktop.
            App.DataDir = Path.Combine(App.AppDir, "data");
            Directory.CreateDirectory(App.DataDir);
            Log.PathName = Path.Combine(App.DataDir, "log.txt");
            var cfg = Config.Load(Path.Combine(App.AppDir, "config.ini"));

            // LiveEarth.exe --render out.png [--time 2026-10-10T12:00:00Z] [--size 1920x1080] [--set key=value ...]
            // LiveEarth.exe --timelapse outDir --frames 72 --step 20 [--time start] [--size 960x540] [--set key=value ...]
            int ri = Array.IndexOf(args, "--render"), ti = Array.IndexOf(args, "--timelapse");
            if ((ri >= 0 && ri + 1 < args.Length) || (ti >= 0 && ti + 1 < args.Length))
            {
                DateTime utc = DateTime.UtcNow; var size = new Size(1920, 1080);
                int frames = 72; double step = 20;
                for (int i = 0; i < args.Length - 1; i++)
                {
                    if (args[i] == "--time") utc = DateTime.Parse(args[i + 1], CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal);
                    if (args[i] == "--size") { var p = args[i + 1].Split('x'); size = new Size(int.Parse(p[0]), int.Parse(p[1])); }
                    if (args[i] == "--mode") cfg.Mode = args[i + 1];
                    if (args[i] == "--set") cfg.Apply(args[i + 1]);
                    if (args[i] == "--frames") frames = int.Parse(args[i + 1]);
                    if (args[i] == "--step") step = double.Parse(args[i + 1], CultureInfo.InvariantCulture);
                }
                var r = new Renderer();
                if (ri >= 0)
                {
                    var fr = App.RenderOnce(cfg, r, utc, size, args[ri + 1]);
                    File.WriteAllText(args[ri + 1] + ".txt", "render_ms=" + fr.Ms + "\r\n");
                }
                else
                {
                    Directory.CreateDirectory(args[ti + 1]);
                    for (int f = 0; f < frames; f++)
                    {
                        var fr = App.RenderOnce(cfg, r, utc.AddMinutes(step * f), size, Path.Combine(args[ti + 1], string.Format("frame_{0:D3}.png", f)));
                        fr.Image.Dispose();
                    }
                }
                return;
            }
            if (Array.IndexOf(args, "--restore") >= 0) { App.RestoreWallpaper(); return; }

            bool created;
            using (var mutex = new Mutex(true, "LiveEarthWallpaper_SingleInstance", out created))
            {
                if (!created) return;
                Native.EnableEcoMode();
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                Application.Run(new App(cfg));
            }
        }
    }
}
