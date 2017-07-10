﻿using System;
using ShaRPG.Util;

namespace ShaRPG.Entity.Components {
    public abstract class AbstractComponent : IComponent {
        protected GameEntity _entity;

        protected AbstractComponent(GameEntity entity) {
            _entity = entity;
        }

        public void Render(IRenderSurface renderSurface) { }

        public abstract void Update(float delta);
        public abstract void Message(IComponentMessage componentMessage);

        protected void Dependency<T>() {
            Dependency(typeof(T));
        }
        
        protected void Dependency(params Type[] types) {
            foreach (Type type in types) {
                if (_entity.GetType().GetMethod("GetComponent").MakeGenericMethod(type).Invoke(_entity, null) == null) {
                    throw new EntityException(
                        _entity,
                        $"Component {GetType().Name} has an unresolved dependency on {type.Name}"
                    );
                }
            }
        }
    }
}
