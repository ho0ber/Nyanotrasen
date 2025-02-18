using Content.Shared.Actions;
using Content.Shared.Actions.ActionTypes;
using Content.Shared.Abilities.Psionics;
using Content.Shared.Speech;
using Content.Shared.Stealth.Components;
using Content.Shared.Damage;
using Content.Server.Players;
using Content.Server.MobState;
using Content.Server.Popups;
using Content.Server.Psionics;
using Robust.Shared.Prototypes;
using Robust.Shared.Timing;
using Robust.Server.GameObjects;
using Robust.Shared.Player;

namespace Content.Server.Abilities.Psionics
{
    public sealed class MindSwapPowerSystem : EntitySystem
    {
        [Dependency] private readonly IPrototypeManager _prototypeManager = default!;
        [Dependency] private readonly SharedActionsSystem _actions = default!;
        [Dependency] private readonly MobStateSystem _mobStateSystem = default!;
        [Dependency] private readonly IGameTiming _gameTiming = default!;
        [Dependency] private readonly SharedPsionicAbilitiesSystem _psionics = default!;
        [Dependency] private readonly PopupSystem _popupSystem = default!;

        public override void Initialize()
        {
            base.Initialize();
            SubscribeLocalEvent<MindSwapPowerComponent, ComponentInit>(OnInit);
            SubscribeLocalEvent<MindSwapPowerComponent, ComponentShutdown>(OnShutdown);
            SubscribeLocalEvent<MindSwapPowerActionEvent>(OnPowerUsed);
            SubscribeLocalEvent<MindSwappedComponent, MindSwapPowerReturnActionEvent>(OnPowerReturned);
            SubscribeLocalEvent<MindSwappedComponent, DispelledEvent>(OnDispelled);
            //
            SubscribeLocalEvent<MindSwappedComponent, ComponentInit>(OnSwapInit);
        }

        private void OnInit(EntityUid uid, MindSwapPowerComponent component, ComponentInit args)
        {
            if (!_prototypeManager.TryIndex<EntityTargetActionPrototype>("MindSwap", out var mindSwap))
                return;

            component.MindSwapPowerAction = new EntityTargetAction(mindSwap);
            if (mindSwap.UseDelay != null)
                component.MindSwapPowerAction.Cooldown = (_gameTiming.CurTime, _gameTiming.CurTime + (TimeSpan) mindSwap.UseDelay);
            _actions.AddAction(uid, component.MindSwapPowerAction, null);

            if (TryComp<PsionicComponent>(uid, out var psionic) && psionic.PsionicAbility == null)
                psionic.PsionicAbility = component.MindSwapPowerAction;
        }

        private void OnShutdown(EntityUid uid, MindSwapPowerComponent component, ComponentShutdown args)
        {
            if (_prototypeManager.TryIndex<EntityTargetActionPrototype>("MindSwap", out var action))
                _actions.RemoveAction(uid, new EntityTargetAction(action), null);
        }

        private void OnPowerUsed(MindSwapPowerActionEvent args)
        {
            if (!(TryComp<DamageableComponent>(args.Target, out var damageable) && damageable.DamageContainerID == "Biological"))
                return;

            if (HasComp<PsionicInsulationComponent>(args.Target))
                return;

            Swap(args.Performer, args.Target);

            _psionics.LogPowerUsed(args.Performer, "mind swap");
            args.Handled = true;
        }

        private void OnPowerReturned(EntityUid uid, MindSwappedComponent component, MindSwapPowerReturnActionEvent args)
        {
            if (HasComp<PsionicInsulationComponent>(component.OriginalEntity) || HasComp<PsionicInsulationComponent>(uid))
                return;

            // How do we get trapped?
            // 1. Original target doesn't exist
            if (!component.OriginalEntity.IsValid() || Deleted(component.OriginalEntity))
            {
                GetTrapped(uid);
                return;
            }
            // 1. Original target is no longer mindswapped
            if (!TryComp<MindSwappedComponent>(component.OriginalEntity, out var targetMindSwap))
            {
                GetTrapped(uid);
                return;
            }

            // 2. Target has undergone a different mind swap
            if (targetMindSwap.OriginalEntity != uid)
            {
                GetTrapped(uid);
                return;
            }

            // 3. Target is dead
            if (_mobStateSystem.IsDead(component.OriginalEntity))
            {
                GetTrapped(uid);
                return;
            }

            Swap(uid, component.OriginalEntity, true);
        }

        private void OnDispelled(EntityUid uid, MindSwappedComponent component, DispelledEvent args)
        {
            Swap(uid, component.OriginalEntity, true);
            args.Handled = true;
        }

        private void OnSwapInit(EntityUid uid, MindSwappedComponent component, ComponentInit args)
        {
            if (_prototypeManager.TryIndex<InstantActionPrototype>("MindSwapReturn", out var mindSwap))
            {
                var action = new InstantAction(mindSwap);
                action.Cooldown = (_gameTiming.CurTime, _gameTiming.CurTime + TimeSpan.FromSeconds(15));
                _actions.AddAction(uid, action, null);
            }
        }

        public void Swap(EntityUid performer, EntityUid target, bool end = false)
        {
            if (end && (!HasComp<MindSwappedComponent>(performer) || !HasComp<MindSwappedComponent>(target)))
                return;

            TryComp<ActorComponent>(performer, out var perfActor);
            TryComp<ActorComponent>(target, out var targetActor);

            if (perfActor != null)
                perfActor.PlayerSession.ContentData()?.Mind?.TransferTo(target, true);

            if (targetActor != null)
                targetActor.PlayerSession.ContentData()?.Mind?.TransferTo(performer, true);

            if (end)
            {
                if (_prototypeManager.TryIndex<InstantActionPrototype>("MindSwapReturn", out var mindSwap))
                {
                    _actions.RemoveAction(performer, new InstantAction(mindSwap), null);
                    _actions.RemoveAction(target, new InstantAction(mindSwap), null);
                }

                RemComp<MindSwappedComponent>(performer);
                RemComp<MindSwappedComponent>(target);
                return;
            }

            var perfComp = EnsureComp<MindSwappedComponent>(performer);
            var targetComp = EnsureComp<MindSwappedComponent>(target);

            perfComp.OriginalEntity = target;
            targetComp.OriginalEntity = performer;
        }

        private void GetTrapped(EntityUid uid)
        {
            if (!_prototypeManager.TryIndex<InstantActionPrototype>("MindSwapReturn", out var action))
                return;

            _popupSystem.PopupEntity(Loc.GetString("mindswap-trapped"), uid, uid, Shared.Popups.PopupType.LargeCaution);
            _actions.RemoveAction(uid, action);

            if (HasComp<TelegnosticProjectionComponent>(uid))
            {
                RemComp<PsionicallyInvisibleComponent>(uid);
                RemComp<StealthComponent>(uid);
                EnsureComp<SharedSpeechComponent>(uid);
                EnsureComp<DispellableComponent>(uid);
                MetaData(uid).EntityName = Loc.GetString("telegnostic-trapped-entity-name");
                MetaData(uid).EntityDescription = Loc.GetString("telegnostic-trapped-entity-desc");
            }
        }
    }

    public sealed class MindSwapPowerActionEvent : EntityTargetActionEvent {}
    public sealed class MindSwapPowerReturnActionEvent : InstantActionEvent {}
}
