﻿using ShaRPG.Util.Coordinate;

namespace ShaRPG.GUI {
    public interface IGuiComponentContainer : IGuiComponent {
        void AddComponent(IGuiComponent component);
        void RemoveComponent(IGuiComponent component);
        bool HasComponent(IGuiComponent component);
        ScreenCoordinate ChildScreenPosition(IGuiComponent component);
    }
}
