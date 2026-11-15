using System;
using System.Drawing;

namespace ClaudeAccountSync
{
    enum PetReaction { None, Pat, Wave, Tea }

    // Visual-only state. Callers supply a local pointer and elapsed time; this object
    // never observes input globally, starts a task, or changes the resident's work state.
    sealed class PetInteraction
    {
        int elapsed, sinceClick = 220, pats;
        public float LookX { get; private set; }
        public float LookY { get; private set; }
        public PetReaction Reaction { get; private set; }
        public int ReactionFrame { get { return elapsed / 40; } }
        public string Message { get; private set; }
        public bool IsTea { get { return Reaction == PetReaction.Tea; } }

        public void Advance(PointF? localMouse, int milliseconds = 40)
        {
            if (milliseconds <= 0) return;
            sinceClick = (int)Math.Min(220L, (long)sinceClick + milliseconds);
            float targetX = 0, targetY = 0;
            if (localMouse.HasValue && Finite(localMouse.Value.X) && Finite(localMouse.Value.Y))
            {
                targetX = (float)(Math.Tanh((localMouse.Value.X - 20.0) / 80 * 2.8) * 2.8);
                targetY = (float)(Math.Tanh((localMouse.Value.Y - 17.0) / 65 * 2.8) * 1.25);
            }
            float ease = (float)(1 - Math.Exp(-Math.Min(milliseconds, 1000) / 95.0));
            LookX += (targetX - LookX) * ease; LookY += (targetY - LookY) * ease;
            if (Reaction != PetReaction.None)
            {
                int duration = Reaction == PetReaction.Pat ? 2200 : Reaction == PetReaction.Wave ? 1900 : 4300;
                elapsed = (int)Math.Min(duration, (long)elapsed + milliseconds);
                if (elapsed >= duration) ClearReaction();
            }
        }

        static bool Finite(float value) { return !float.IsNaN(value) && !float.IsInfinity(value); }

        public bool TryReact(PetReaction reaction)
        {
            if (reaction != PetReaction.Pat && reaction != PetReaction.Wave && reaction != PetReaction.Tea) return false;
            if (sinceClick < 220 || (reaction == PetReaction.Tea && IsTea)) return false;
            Reaction = reaction; elapsed = 0; sinceClick = 0;
            if (reaction == PetReaction.Pat)
            {
                string[] replies = { "嘿嘿，收到啦。", "再摸摸，就要飘起来了。", "今天也会认真陪你。" };
                Message = replies[pats % replies.Length]; pats = (pats + 1) % replies.Length;
            }
            else Message = reaction == PetReaction.Tea ? "谢谢！一起歇一小会。" : "在呢！我看到你啦。";
            return true;
        }

        public void ClearReaction()
        { Reaction = PetReaction.None; elapsed = 0; Message = null; }
    }
}
