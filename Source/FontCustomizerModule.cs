﻿using FMOD.Studio;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Monocle;
using MonoMod.Cil;
using MonoMod.Utils;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using System.Xml;

namespace Celeste.Mod.FontCustomizer
{
    public class FontCustomizerModule : EverestModule
    {
        public static readonly string basic_path = "Assets/FontFile";
        public static FontCustomizerModule Instance { get; private set; }

        public override Type SettingsType => typeof(FontCustomizerModuleSettings);
        public static FontCustomizerModuleSettings Settings => (FontCustomizerModuleSettings)Instance._Settings;

        public override Type SessionType => typeof(FontCustomizerModuleSession);
        public static FontCustomizerModuleSession Session => (FontCustomizerModuleSession)Instance._Session;
        public static readonly HashSet<string> FreeTypeExtension = ["ttf", "otf", "pfb", "pfm", "cid", "cff", "fon", "fnt", "pcf"];
        public override void CreateModMenuSection(TextMenu menu, bool inGame, EventInstance snapshot)
        {
            base.CreateModMenuSection(menu, inGame, snapshot);
            if (!inGame)
            {
                ExtendMenu(menu, inGame);
            }
        }
        private void ExtendMenu(TextMenu menu, bool inGame)
        {
            OptionConfirmed<string?> skinSelectMenu = new(Dialog.Clean("USSRNAME_FontCustomizer_FontName"));

            skinSelectMenu.Add(Dialog.Clean("USSRNAME_FontCustomizer_VanillaFont"), null, true);

            foreach (var v in foundFonts)
            {
                var name = v.PathVirtual[(basic_path.Length + 1)..];
                var nameless = name[..name.LastIndexOf('.')];
                if (!Dialog.Languages.Values.Select(x => x.FontFace).Contains(nameless))
                {
                    skinSelectMenu.Add(nameless, name, name == Settings.FontName);
                }
            }

            skinSelectMenu.Change(x =>
            {
                Settings.FontName = x;
                LoadFont(Settings.FontName);
            });

            menu.Add(skinSelectMenu);
        }

        public FontCustomizerModule()
        {
            Instance = this;
#if DEBUG
            // debug builds use verbose logging
            Logger.SetLogLevel(nameof(FontCustomizerModule), LogLevel.Verbose);
#else
            // release builds use info logging to reduce spam in log files
            Logger.SetLogLevel(nameof(NewFontModule), LogLevel.Info);
#endif
            //var t2d = new Texture2D(Engine.Graphics.GraphicsDevice, 64, 64, false, SurfaceFormat.Alpha8);
            ////t2d.SetData();
            //MemoryStream ms = new();
            //ms.ReadByte();
            //t2d.SaveAsPng(ms, t2d.Width, t2d.Height);

        }
        SharpFont.Library library = new();
        SharpFont.Face current;
        double baseline;
        double scale;
        double targetSize;

        public List<ModAsset> foundFonts = [];
        public override void LoadContent(bool firstLoad)
        {
            base.LoadContent(firstLoad);
            foreach (var (k, v) in Everest.Content.Map)
            {
                if (k.StartsWith(basic_path) && FreeTypeExtension.Contains(v.Format)/* && !vanillaFonts.ContainsValue(k[(basic_path.Length + 1)..])*/)
                {
                    foundFonts.Add(v);
                }
            }
            if (Settings.FontName is not null && !Everest.Content.TryGet($"{basic_path}/{Settings.FontName}", out _))
            {
                Settings.FontName = null;
            }
        }
        IEnumerable<char> generate_all()
        {
            var p = current.GetFirstChar(out var i);
            while (i != 0)
            {
                yield return (char)p;
                p = current.GetNextChar(p, out i);
            }
        }
        public void ReloadFont(string? font_ext)
        => ReloadFont(font_ext, Dialog.Language);
        public void LoadFont(string? font_ext)
        => LoadFont(font_ext, Dialog.Language);
        public void ReloadFont(string? font_ext, string lang)
        => ReloadFont(font_ext, Dialog.Languages[lang]);
        public void LoadFont(string? font_ext, string lang)
        => LoadFont(font_ext, Dialog.Languages[lang]);
        public void ReloadFont(string? font_ext, Language lang) => LoadFont(font_ext, lang);

        //called when Language changes.
        //or font changes.
        public void LoadFont(string? font_ext, Language lang)
        {
            if (font_ext == "")
            {
                font_ext = null;
            }
            var _param1 = font_ext;
            if (font_ext is null)
            {
                font_ext = foundFonts
                    .Select(x => x.PathVirtual[(basic_path.Length + 1)..])
                    .FirstOrDefault(x => x[..x.LastIndexOf('.')] == lang.FontFace)
                    ?? "";
            }

            if (Everest.Content.TryGet($"{basic_path}/{font_ext}", out var md) && FreeTypeExtension.Contains(md.Format))
            {
                if (cachedFonts.TryGetValue(font_ext, out var rr))
                {
                    current = rr;
                }
                else
                {
                    current = new SharpFont.Face(library, md.Data, 0);
                    cachedFonts[font_ext] = current;
                }

                current.SetPixelSizes(0, 64);

                scale = lang.FontFaceSize / current.Size.Metrics.Height.ToDouble();
                if (lang.FontFace == "Renogare")
                {
                    scale *= scale;//why vanilla font smaller than mine?
                }
                if (current.IsScalable)
                {
                    SharpFont.FTMatrix mat = new()
                    {
                        XX = (SharpFont.Fixed16Dot16)scale,
                        YY = (SharpFont.Fixed16Dot16)scale,
                        XY = 0,
                        YX = 0,
                    };
                    current.SetTransform(mat);
                }
                else
                {
                    //
                    scale = 1;
                    current.SetPixelSizes((uint)(lang.FontFaceSize * (lang.FontFaceSize / current.Size.Metrics.Height.ToDouble())), 0);
                }
                baseline = (-current.Size.Metrics.Descender.ToDouble() * scale);
                targetSize = lang.FontFaceSize;

                lang.Font.Sizes[0].Characters.Clear();

                ThreadCancel = true;
                RenderTask?.Wait();
                ThreadCancel = false;

                var tar = Settings.Strategy switch
                {
                    GenerationStrategy.Dialog => lang.Cleaned.Values.SelectMany(x => x).Concat(englishGenerated).Distinct(),
                    GenerationStrategy.Loaded => fallbacks[lang.FontFace].Keys.Select(x => (char)x),
                    GenerationStrategy.All => generate_all(),
                    GenerationStrategy.LazyLoad => Enumerable.Empty<char>(),

                    _ => throw new NotImplementedException(),
                };
                RenderTask = new(() =>
                {
                    RenderThread(lang.FontFace, tar);
                });
                RenderTask.Start();

                //switch (Settings.Strategy)
                //{
                //    case GenerationStrategy.Dialog:

                //        foreach (var c in lang.Cleaned.Values.SelectMany(x => x).Concat(englishGenerated).Distinct())
                //        {
                //            LockedGenerateOrFallbackAndSave(c, lang.FontFace);
                //        }

                //        break;
                //    case GenerationStrategy.Loaded:
                //        foreach (var c in fallbacks[lang.FontFace].Keys)
                //        {
                //            LockedGenerateOrFallbackAndSave((char)c, lang.FontFace);
                //        }
                //        break;
                //    case GenerationStrategy.LazyLoad:
                //        break;
                //    case GenerationStrategy.All:
                //        var p = current.GetFirstChar(out var i);
                //        while (i != 0)
                //        {
                //            LockedGenerateOrFallbackAndSave((char)p, lang.FontFace);
                //            p = current.GetNextChar(p, out i);
                //        }
                //        break;
                //};

            }
            else
            {
                //TODO: install vanilla font here.
            }
        }
        //public Dictionary<string, string> vanillaFonts = new()
        //{
        //    {"schinese", "Noto Sans CJK SC Medium.otf" },
        //    {"japanese", "Noto Sans CJK JP Medium.otf"},
        //    {"koreana", "Noto Sans CJK KR Medium.otf"},
        //    {"russian", "Noto Sans Med.ttf" },
        //    {"spanish", "Renogare.otf" },
        //    {"english", "Renogare.otf" },
        //    {"french", "Renogare.otf" },
        //    {"german", "Renogare.otf" },
        //    {"italian", "Renogare.otf" },
        //};

        static XmlElement fake_elem;

        static FontCustomizerModule()
        {
            XmlDocument doc = new();
            fake_elem = doc.CreateElement("m");
            fake_elem.SetAttribute("x", "0");
            fake_elem.SetAttribute("y", "0");
        }

        public void LockededMerge()
        {
            foreach (var (fnt, dir) in RenderTarget)
            {
                foreach(var (ch, tex) in dir)
                {
                    var mightunload = Fonts.Get(fnt);
                    if (mightunload is null)
                    {
                        break;
                    }
                    mightunload.Sizes[0].Characters[ch] = tex;
                    //DynamicData.For(mightunload).Get<List<VirtualTexture>>("managedTextures").Add(tex.Texture.Texture);
                }
                dir.Clear();
            }
        }
        public PixelFontCharacter? LockedGet(char ch, string fontvanilla)
        {
            lock (this)
            {
                if (!RenderTarget.TryGetValue(fontvanilla, out var dir) || !dir.TryGetValue(ch, out PixelFontCharacter? ret))
                {
                    ret = LockedGenerateOrFallbackAndSave(ch, fontvanilla);
                }
                LockededMerge();
                return ret;
            }
        }
        //no need to concurrent.
        //just lock it manually.
        //there're only two threads, after all.
        Dictionary<string, Dictionary<int, PixelFontCharacter>> RenderTarget = [];
        Dictionary<string, Queue<(ulong, VirtualTexture)>> Disposer = [];
        ////Don't support more than one thread.
        //string ThreadFont;
        //char? ThreadRequest;
        //PixelFontCharacter? ThreadResult;

        Task RenderTask;
        volatile bool ThreadCancel = false;
        public void RenderThread(string vanilla, /*HashSet<char>*/IEnumerable<char> gen)
        {
            foreach (var c in gen)
            {
                //oh, just lock it. 
                loadimmediately = Environment.CurrentManagedThreadId;
                LockedGenerateOrFallbackAndSave(c, vanilla);
                System.Threading.Thread.Sleep(1);//laggy
                if (ThreadCancel)
                {
                    RenderTarget[vanilla].Clear();
                    ThreadCancel = false;
                    return;
                }
            }
            loadimmediately = int.MaxValue + 42L + int.MaxValue;
            //Stopwatch sw = new();
            //char n;
            //ThreadFont = vanilla;
            //while (!ThreadCancel && gen.Count > 0)
            //{
            //    if (ThreadRequest is not null)
            //    {
            //        sw.Start();
            //        n = ThreadRequest.Value;
            //    }
            //    else if (sw.IsRunning && sw.ElapsedMilliseconds < 18)
            //    {
            //        //request char for multiple timesin 1 frame
            //        System.Threading.Thread.Sleep(0);
            //        continue;
            //    }
            //    else
            //    {
            //        if (sw.IsRunning)
            //        {
            //            sw.Stop();
            //        }
            //        n = gen.First();
            //    }
            //    var r = GenerateOrFallbackAndSave(n, vanilla);
            //    if (ThreadRequest is not null && n == ThreadRequest)
            //    {
            //        ThreadResult = r;
            //        ThreadRequest = null;
            //    }
            //    gen.Remove(n);
            //}
        }

        public PixelFontCharacter? LockedGenerateOrFallbackAndSave(char c, string fontvanilla)
        {
            lock (this)
            {
                PixelFontCharacter? Fallback()
                {
                    if (fallbacks.TryGetValue(fontvanilla, out var res))
                    {
                        if (res.TryGetValue(c, out var chars))
                        {
                            return RenderTarget[fontvanilla][c] = chars;
                            //return Fonts.Get(fontvanilla).Sizes[0].Characters[c] = chars;
                        }
                    }
                    return null;
                }
                if (current is null)
                {
                    return Fallback();
                }
                if (Emoji.Start <= c && c <= Emoji.End)
                {
                    return Fallback();
                }

                var tex = LockededGenerateChar(c, current, _make_unique: fontvanilla);

                if (tex is null)
                {
                    return Fallback();
                }

                SharpFont.GlyphSlot glyph = current.Glyph;
                SharpFont.BBox box = glyph.GetGlyph().GetCBox(SharpFont.GlyphBBoxMode.Pixels);

                fake_elem.SetAttribute("width", tex.Width.ToString());
                fake_elem.SetAttribute("height", tex.Height.ToString());
                fake_elem.SetAttribute("xoffset", box.Left.ToString());
                fake_elem.SetAttribute("yoffset", ((int)(targetSize - box.Top - baseline)).ToString());
                fake_elem.SetAttribute("xadvance", glyph.Advance.X.ToInt32().ToString());

                var charx = new PixelFontCharacter(c, tex, fake_elem);
                //return Fonts.Get(fontvanilla).Sizes[0].Characters[c] = charx;
                Disposer[fontvanilla].Enqueue((make_unique, charx.Texture.Texture));
                return RenderTarget[fontvanilla][c] = charx;
            }
        }
        public Dictionary<string, Dictionary<int, PixelFontCharacter>> fallbacks = [];
        static ulong make_unique = 0;
        public MTexture? LockededGenerateChar(char c, SharpFont.Face lang, string _make_unique)
        {
            var wh = lang.GetCharIndex(c);
            if (wh == 0)
            {
                return null;
            }
            lang.LoadGlyph(wh, SharpFont.LoadFlags.Render, SharpFont.LoadTarget.Normal);

            var bmp = lang.Glyph.Bitmap;
            if (bmp.Width == 0 || bmp.Rows == 0)
            {
                return null;
            }
            Color[] data = new Color[bmp.Width * bmp.Rows];
            for (int i = 0; i < bmp.Rows; i++)
            {
                for (int j = 0; j < bmp.Width; j++)
                {
                    var vv = bmp.BufferData[i * bmp.Pitch + j];
                    data[i * bmp.Width + j] = new(vv, vv, vv, vv);
                }
            }
            var vt = VirtualContent.CreateTexture($"ussrname_{nameof(FontCustomizer)}_{_make_unique}_{c}_{++make_unique}", bmp.Width, bmp.Rows, Color.White);
            System.Threading.Thread.GetCurrentProcessorId();
            vt.Texture_Safe.SetData(data);
            var mtex = new MTexture(vt);
            bmp.Dispose();
            return mtex;
        }

        Dictionary<string, SharpFont.Face> cachedFonts = [];
        Dictionary<SharpFont.Face, Dictionary<int, MTexture>> cachedChars = [];
        public readonly IEnumerable<char> englishGenerated = //from Celeste\Content\Dialog\Fonts\configs\renogare.bmfc
            new (int f, int t)[] { (32, 126), (160, 163), (165, 180), (182, 263), (268, 275), (278, 283), (286, 287), (290, 291), (298, 299), (302, 305), (310, 311), (313, 318), (321, 321), (322, 328), (332, 333), (336, 347), (350, 357), (362, 363), (366, 371), (376, 382), (536, 539), (710, 711), (728, 733), (1460, 1460), (8211, 8212), (8216, 8218), (8220, 8222), (8224, 8226), (8230, 8230), (8240, 8240), (8249, 8250), (8260, 8260), (8364, 8364), (8482, 8482), (8800, 8800), (8804, 8805) }
            .SelectMany(x => Enumerable.Range(x.f, x.t - x.f + 1))
            .Select(x => (char)x);
        //public static Dictionary<string, string> _GetLanguage = new()
        //{
        //    { "Noto Sans CJK SC Medium" , "schinese" },
        //    {"Renogare" , "english" },//The Renogare Language is better. English/Brazilian Portuguese/French/German/Italian/Spanish all use the same fonts.
        //    {"Noto Sans CJK JP Medium" , "japanese" },
        //    {"Noto Sans CJK KR Medium" , "koreana" },
        //    {"Noto Sans Med" , "russian" },
        //};
        //public static string GetLanguage(string face)
        //{
        //    if(_GetLanguage.TryGetValue(face, out var rface))
        //    {
        //        return rface;
        //    }
        //    return face;
        //}
        static MethodInfo[] orig_draw =
            [
                .. typeof(PixelFontSize)
                .GetMethods()
                .Where(x => x.Name == "orig_Draw")
                .OrderBy(x => x.GetParameters().Length),
            ];
        static MethodInfo orig_measure = typeof(PixelFontSize).GetMethod("orig_Measure")!;
        static MonoMod.RuntimeDetour.ILHook orig_draw_s;
        static MonoMod.RuntimeDetour.ILHook orig_draw_l;
        static MonoMod.RuntimeDetour.ILHook orig_measure_h;
        public override void Unload()
        {
            ThreadCancel = true;
            RenderTask?.Wait();
            // TODO: unapply any hooks applied in Load()
            On.Celeste.Fonts.Load -= Fonts_Load;
            On.Celeste.Settings.ApplyLanguage -= Settings_ApplyLanguage;
            IL.Celeste.GameLoader.LoadThread -= GameLoader_LoadThread;
            On.Monocle.PixelFont.Get -= PixelFont_Get;
            IL.Monocle.PixelFontSize.Draw_string_Vector2_Vector2_Vector2_Color_float_Color_float_Color -= GenericForeachPatcher;
            IL.Monocle.PixelFontSize.Get -= GenericForeachPatcher;
            IL.Monocle.PixelFontSize.Measure_char -= GenericForeachPatcher;
            IL.Monocle.PixelFontSize.WidthToNextLine -= GenericForeachPatcher;
            orig_draw_s?.Dispose();
            orig_draw_l?.Dispose();
            orig_measure_h?.Dispose();

            IL.Monocle.VirtualTexture.Load -= VirtualTexture_Load;
            On.Monocle.Engine.UnloadContent -= Engine_UnloadContent;
        }
        public override void Load()
        {
            // TODO: apply any hooks that should always be active
            On.Celeste.Fonts.Load += Fonts_Load;
            On.Celeste.Settings.ApplyLanguage += Settings_ApplyLanguage;
            IL.Celeste.GameLoader.LoadThread += GameLoader_LoadThread;
            On.Monocle.PixelFont.Get += PixelFont_Get;
            IL.Monocle.PixelFontSize.Draw_string_Vector2_Vector2_Vector2_Color_float_Color_float_Color += GenericForeachPatcher;
            IL.Monocle.PixelFontSize.Get += GenericForeachPatcher;
            IL.Monocle.PixelFontSize.Measure_char += GenericForeachPatcher;
            IL.Monocle.PixelFontSize.WidthToNextLine += GenericForeachPatcher;
            orig_draw_s = new(orig_draw[0], GenericForeachPatcher);
            orig_draw_l = new(orig_draw[1], GenericForeachPatcher);
            orig_measure_h = new(orig_measure, GenericForeachPatcher);

            IL.Monocle.VirtualTexture.Load += VirtualTexture_Load;
            On.Monocle.Engine.UnloadContent += Engine_UnloadContent;
        }

        private void Engine_UnloadContent(On.Monocle.Engine.orig_UnloadContent orig, Engine self)
        {
            ThreadCancel = true;
            RenderTask?.Wait();
            orig(self);
        }

        private void VirtualTexture_Load(ILContext il)
        {
            var ic = new ILCursor(il);
            if (ic.TryGotoNext(MoveType.After,
                i => i.MatchLdarg(0),
                i => i.MatchCallOrCallvirt(typeof(VirtualTexture), "get_LoadImmediately"),
                i => i.MatchBrfalse(out _)))
            {
                var label = ic.MarkLabel();
                ic.Index -= 3;
                ic.Emit(Mono.Cecil.Cil.OpCodes.Call, typeof(FontCustomizerModule).GetMethod(nameof(GetProcess), BindingFlags.Static | BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic));
                //ic.Emit(Mono.Cecil.Cil.OpCodes.Ldsfld, typeof(FontCustomizerModule).GetField(nameof(loadimmediately), BindingFlags.Static | BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic));
                ic.Emit(Mono.Cecil.Cil.OpCodes.Brtrue, label);
            }
        }
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        static bool GetProcess()
        {
            return loadimmediately == Environment.CurrentManagedThreadId;
        }
        static long loadimmediately = int.MaxValue + 42L + int.MaxValue;//Impossible to be an existing thread id.
        private PixelFontSize PixelFont_Get(On.Monocle.PixelFont.orig_Get orig, PixelFont self, float size)
        {
            if (self?.Sizes?.Count == 0)
            {
                return null!;
            }
            return self!.Sizes[0];
        }


        private void GenericForeachPatcher(ILContext il)
        {
            var ic = new ILCursor(il);
            while (ic.TryGotoNext(MoveType.Before,
                i => i.MatchCallOrCallvirt(typeof(Dictionary<int, PixelFontCharacter>), "TryGetValue")))
            {
                ic.Remove();
                ic.Emit(Mono.Cecil.Cil.OpCodes.Ldarg_0);
                ic.EmitDelegate(PatchWorker);
            }
        }
        bool PatchWorker(Dictionary<int, PixelFontCharacter> dir, int o, out PixelFontCharacter? px, PixelFontSize self)
        {
            bool b = dir.TryGetValue(o, out px);
            if (b == false)
            {

                var lang = Dialog.Languages.Values.FirstOrDefault(x => x.Font is not null && x.FontSize == self);
                if (lang != null)
                {
                    px = LockedGet((char)o, lang.FontFace);
                }
                if (px is null)
                {
                    if (!fallbacks.TryGetValue(lang?.FontFace ?? "", out var dirfb) || !dirfb.TryGetValue(o, out px))
                    {
                        return false;
                    }
                }
            }
            return true;
        }
        private void GameLoader_LoadThread(ILContext il)
        {
            var ic = new ILCursor(il);
            ic.Index = -1;
            if (ic.TryGotoPrev(MoveType.After, i => i.MatchCall(typeof(Fonts), "Load")))
            {
                ic.EmitDelegate(() =>
                {
                    LoadFont(Settings.FontName);
                });
            }
        }

        private void Settings_ApplyLanguage(On.Celeste.Settings.orig_ApplyLanguage orig, Settings self)
        {
            orig(self);
            LoadFont(Settings.FontName);
        }

        private PixelFont Fonts_Load(On.Celeste.Fonts.orig_Load orig, string face)
        {
            var ret = orig(face);
            if (!fallbacks.ContainsKey(face))
            {
                lock (this)
                {
                    fallbacks.TryAdd(face, new(ret.Sizes[0].Characters));
                    RenderTarget.TryAdd(face, new());
                    Disposer.TryAdd(face, new());
                }
            }
            return ret;
        }



    }
}