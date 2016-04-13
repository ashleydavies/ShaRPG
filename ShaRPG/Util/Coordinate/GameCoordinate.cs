﻿using ShaRPG.Map;

namespace ShaRPG.Util.Coordinate
{
    public class GameCoordinate : Coordinate
    {
        public GameCoordinate(int x, int y) : base(x, y) {}

        public static implicit operator TileCoordinate(GameCoordinate gameCoordinate)
        {
            return new TileCoordinate(0, 0);
        }

        public static GameCoordinate operator +(GameCoordinate a, GameCoordinate b)
        {
            return new GameCoordinate(a.X + b.X, a.Y + b.Y);
        }

    }
}