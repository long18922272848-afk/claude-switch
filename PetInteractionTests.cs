using System;
using System.Drawing;
using System.Drawing.Drawing2D;

namespace ClaudeAccountSync
{
    static class PetInteractionTests
    {
        static int passed;
        static void Check(bool ok, string name)
        { if (!ok) throw new Exception("Pet interaction: " + name); passed++; }

        static PetInteraction Look(float x, float y)
        { var value = new PetInteraction(); value.Advance(new PointF(x, y), 1000); return value; }

        static Bitmap Draw(PetState state, int frame, PetInteraction interaction, bool left)
        {
            var result = new Bitmap(90, 60);
            using (var g = Graphics.FromImage(result))
            { g.Clear(Color.Transparent); PetScene.Sprite(g, 10, 10, state, frame, left, interaction); }
            return result;
        }

        static bool Same(Bitmap a, Bitmap b)
        {
            for (int y = 0; y < a.Height; y++) for (int x = 0; x < a.Width; x++)
                if (a.GetPixel(x, y).ToArgb() != b.GetPixel(x, y).ToArgb()) return false;
            return true;
        }

        static float EyeX(Bitmap image)
        {
            int count = 0, sum = 0, ink = Color.FromArgb(47, 39, 34).ToArgb();
            for (int y = 23; y < 31; y++) for (int x = 10; x < 45; x++)
                if (image.GetPixel(x, y).ToArgb() == ink) { count++; sum += x; }
            if (count == 0) throw new Exception("Pet eye pixels missing");
            return sum / (float)count;
        }

        public static int Run()
        {
            passed = 0;
            var look = new PetInteraction();
            look.Advance(new PointF(10000, -10000));
            Check(look.LookX > 0 && look.LookX < 2.8f && look.LookY < 0 && look.LookY > -1.25f, "gaze eases toward pointer without jumping or leaving face");
            look.Advance(new PointF(10000, -10000), 1000);
            Check(look.LookX > 2.7f && look.LookX <= 2.8f && look.LookY >= -1.25f, "distant pointer stays within facial bounds");
            look.Advance(null, 1000);
            Check(Math.Abs(look.LookX) < .001 && Math.Abs(look.LookY) < .001, "pointer leaving returns gaze to centre");
            look.Advance(new PointF(float.NaN, float.PositiveInfinity));
            Check(!float.IsNaN(look.LookX) && !float.IsNaN(look.LookY), "invalid pointer coordinates do not poison animation");

            var reaction = new PetInteraction();
            Check(reaction.TryReact(PetReaction.Pat) && reaction.Message.Contains("收到"), "head click starts its visual response");
            Check(!reaction.TryReact(PetReaction.Wave) && reaction.Reaction == PetReaction.Pat, "immediate repeat click is throttled");
            reaction.Advance(null, 220);
            Check(reaction.TryReact(PetReaction.Wave) && reaction.ReactionFrame == 0, "later body click replaces rather than queues a response");
            reaction.Advance(null, 1899);
            Check(reaction.Reaction == PetReaction.Wave, "wave remains active until its duration");
            reaction.Advance(null, 1);
            Check(reaction.Reaction == PetReaction.None && reaction.Message == null, "wave expires and clears speech");
            Check(reaction.TryReact(PetReaction.Tea) && reaction.IsTea, "cup click enters tea response");
            reaction.Advance(null, 1000);
            Check(!reaction.TryReact(PetReaction.Tea) && reaction.ReactionFrame == 25, "repeated tea click never restarts the sip");
            reaction.Advance(null, int.MaxValue);
            Check(!reaction.IsTea && reaction.Reaction == PetReaction.None, "long suspension safely expires response without overflow");
            Check(!reaction.TryReact(PetReaction.None) && !reaction.TryReact((PetReaction)99), "unknown reactions are rejected");
            reaction.TryReact(PetReaction.Pat); reaction.ClearReaction();
            Check(reaction.Reaction == PetReaction.None && reaction.ReactionFrame == 0 && reaction.Message == null, "clear removes visual response for work transitions");

            Theme.Init("light"); float dpi = Theme.Dpi; Theme.Dpi = 96;
            try
            {
                var left = Look(-10000, 17); var right = Look(10000, 17);
                using (var a = Draw(PetState.Working, 30, left, false))
                using (var b = Draw(PetState.Working, 30, right, false))
                    Check(EyeX(b) - EyeX(a) > 4, "actual eye pixels look toward left and right pointer");
                using (var a = Draw(PetState.Working, 30, left, true))
                using (var b = Draw(PetState.Working, 30, right, true))
                    Check(EyeX(b) - EyeX(a) > 4, "mirrored sprite still looks in screen-space pointer direction");
                using (var a = Draw(PetState.Working, 157, left, false))
                using (var b = Draw(PetState.Working, 157, right, false))
                    Check(Same(a, b), "closed blink stays fixed while pointer moves");
                using (var a = Draw(PetState.Resting, 500, left, false))
                using (var b = Draw(PetState.Resting, 500, right, false))
                    Check(Same(a, b), "sleeping eyes do not chase the pointer");
                var teaLeft = new PetInteraction(); var teaRight = new PetInteraction();
                teaLeft.TryReact(PetReaction.Tea); teaRight.TryReact(PetReaction.Tea);
                teaLeft.Advance(new PointF(-10000, 17), 1400); teaRight.Advance(new PointF(10000, 17), 1400);
                using (var a = Draw(PetState.Working, 30, teaLeft, false))
                using (var b = Draw(PetState.Working, 30, teaRight, false))
                    Check(Same(a, b), "sipping eyes close independently of work frame and pointer");
                using (var desk = new Bitmap(90, 50))
                using (var g = Graphics.FromImage(desk))
                {
                    g.Clear(Color.Transparent); PetScene.Desk(g, 0, 0, true, 30);
                    bool present = desk.GetPixel(71, 27).ToArgb() == Theme.Accent.ToArgb();
                    g.Clear(Color.Transparent); PetScene.Desk(g, 0, 0, true, 30, teaLeft);
                    Check(present && desk.GetPixel(71, 27).ToArgb() != Theme.Accent.ToArgb() && desk.GetPixel(71, 27).A < 255,
                        "held tea removes the desk cup instead of duplicating it");
                }
                using (var image = new Bitmap(220, 180))
                using (var g = Graphics.FromImage(image))
                {
                    g.TranslateTransform(7, 9); g.ScaleTransform(1.5f, 1.5f);
                    using (var original = g.Transform)
                    {
                        foreach (PetState state in Enum.GetValues(typeof(PetState)))
                            foreach (PetReaction kind in Enum.GetValues(typeof(PetReaction)))
                            {
                                var visual = Look(200, 100); visual.TryReact(kind); visual.Advance(new PointF(200, 100), 480);
                                PetScene.Sprite(g, 5, 6, state, 80, true, visual);
                            }
                        using (var after = g.Transform)
                        {
                            bool same = true; for (int i = 0; i < 6; i++) same &= original.Elements[i] == after.Elements[i];
                            Check(same, "all work states and reactions restore caller graphics transform");
                        }
                    }
                }
            }
            finally { Theme.Dpi = dpi; }
            return passed;
        }
    }
}
