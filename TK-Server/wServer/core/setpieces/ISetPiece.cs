﻿using System;
using wServer.core.worlds;

namespace wServer.core.setpieces
{
    public abstract class ISetPiece
    {
        public abstract int Size { get; }
        public virtual string Map { get; }

        public virtual void RenderSetPiece(World world, IntPoint pos)
        {
            if (string.IsNullOrEmpty(Map))
                return;

            var data = world.Manager.Resources.GameData.GetWorldData(Map);
            if (data == null)
            {
                Console.WriteLine($"[{GetType().Name}] Invalid RenderSetPiece {Map}");
                return;
            }
            SetPieces.RenderFromData(world, pos, data);
        }
    }
}
