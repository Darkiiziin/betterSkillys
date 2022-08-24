﻿using common.resources;
using wServer.core;
using wServer.core.objects;

namespace wServer.logic.behaviors
{
    internal class RemoveConditionalEffect : Behavior
    {
        private readonly ConditionEffectIndex _effect;

        public RemoveConditionalEffect(ConditionEffectIndex effect) => _effect = effect;

        protected override void OnStateEntry(Entity host, TickTime time, ref object state) => host.ApplyConditionEffect(new ConditionEffect(_effect, 0));

        protected override void TickCore(Entity host, TickTime time, ref object state)
        { }
    }
}
