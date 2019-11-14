using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using AT_Utils;
using AT_Utils.UI;
using GC.UI;
using KSP.Localization;

namespace GroundConstruction
{
    public interface IRecycler
    {
        float GetRecycleExperienceMod();

        void GetRecycleInfo(
            Part p,
            float efficiency,
            out DIYKit.Requirements assembly_requirements,
            out DIYKit.Requirements construction_requirements
        );

        bool IsRecycling { get; }
        void Recycle(Part p, bool discard_excess_resources, Action<bool> on_finished);
        IEnumerable<string> GetRecycleReport();
    }

    public class RecyclerWindow : UIWindowBase<RecyclerUI>
    {
        private readonly IRecycler recycler;

        private readonly PartsRegistry root_parts;

        public RecyclerWindow(IRecycler recycler) : base(Globals.Instance.AssetBundle)
        {
            this.recycler = recycler;
            root_parts = new PartsRegistry(recycler);
            GameEvents.onVesselWasModified.Add(onVesselModified);
            GameEvents.onVesselCrewWasModified.Add(onRecyclerCrewModified);
            GameEvents.onGameStateSave.Add(onGameSaved);
        }

        ~RecyclerWindow()
        {
            GameEvents.onVesselWasModified.Remove(onVesselModified);
            GameEvents.onVesselCrewWasModified.Remove(onRecyclerCrewModified);
            GameEvents.onGameStateSave.Remove(onGameSaved);
        }

        public void SetVessels(IEnumerable<Vessel> vessels)
        {
            if(recycler != null)
            {
                root_parts.Clear();
                if(Controller != null)
                    Controller.Clear();
                foreach(var vsl in vessels)
                {
                    var rp = root_parts.Add(vsl.rootPart);
                    if(rp != null)
                        on_add_root(rp);
                }
            }
        }

        void onVesselModified(Vessel vsl)
        {
            if(vsl == null || vsl.rootPart == null)
                return;
            if(root_parts.TryGetValue(vsl.rootPart.persistentId, out var rp))
            {
                Utils.Log("onVesselModified: {}", vsl.GetID()); //debug
                rp.Update();
            }
        }

        void onRecyclerCrewModified(Vessel vsl)
        {
            if(vsl == null || recycler == null)
                return;
            if(recycler is PartModule pm && pm.vessel == vsl)
            {
                Utils.Log("onRecyclerCrewModified: {}", vsl.GetID()); //debug
                Update();
            }
        }

        void onGameSaved(ConfigNode _config_node) => this.SaveState();

        void on_add_root(RecyclablePart rp)
        {
            if(Controller != null)
            {
                rp.Update();
                Controller.AddRoot(rp);
            }
        }

        void on_remove_root(uint root_part_id)
        {
            if(Controller != null)
                Controller.DeleteRoot(root_part_id);
        }

        public void SyncVessels(IEnumerable<Vessel> vessels) =>
            root_parts.Sync(vessels.Select(vsl => vsl.rootPart), on_add_root, on_remove_root);

        public void Update() => root_parts.ForEach(rp => rp.Update());

        protected override void init_controller()
        {
            root_parts.ForEach(rp =>
            {
                rp.Update();
                Controller.AddRoot(rp);
            });
            Controller.closeButton.onClick.AddListener(this.Close);
        }
    }

    internal class RecyclablePart : IRecyclable
    {
        private readonly Part part;
        public readonly IRecycler Recycler;
        public uint ID => part != null ? part.persistentId : 0;
        public RecyclableTreeNode Display { get; private set; }
        private DIYKit.Requirements assembly_requirements;
        private DIYKit.Requirements construction_requirements;
        private readonly ChildPartsRegistry children;
        public bool Valid => part != null && part.vessel != null && Recycler != null;
        public static implicit operator bool(RecyclablePart rp) => rp != null && rp.Valid;

        public RecyclablePart(Part part, IRecycler recycler)
        {
            Recycler = recycler;
            this.part = part;
            children = new ChildPartsRegistry(this);
            this.part.children.ForEach(p => children.Add(p));
        }

        public void Update(float efficiency = -1)
        {
            if(efficiency < 0)
                efficiency = Recycler.GetRecycleExperienceMod();
            Recycler.GetRecycleInfo(part,
                efficiency,
                out assembly_requirements,
                out construction_requirements);
            children.Sync(part.children);
            children.ForEach(child =>
            {
                child.Update(efficiency);
                assembly_requirements.Update(child.assembly_requirements);
                construction_requirements.Update(child.construction_requirements);
            });
            update_display();
        }

        public void SetDisplay(RecyclableTreeNode display_node)
        {
            Display = display_node;
            if(assembly_requirements == null || construction_requirements == null)
                Update();
            else
                update_display();
        }

        public IEnumerable<IRecyclable> GetChildren()
        {
            foreach(var child in children)
                yield return child;
        }

        private static string format_resource(DIYKit.Requirements req) =>
            req.Valid
                ? $"{req.resource.name}: {FormatUtils.formatBigValue((float)req.resource_amount, " u")}"
                : "";

        private void update_display()
        {
            if(Display == null)
                return;
            if(part.vessel != null && part == part.vessel.rootPart)
                Display.nodeName.text = Localizer.Format(part.vessel.vesselName);
            else
                Display.nodeName.text = part.partInfo.title;
            Display.assemblyResourceInfo.text = format_resource(assembly_requirements);
            Display.constructionResourceInfo.text = format_resource(construction_requirements);
            var ec = assembly_requirements.energy + construction_requirements.energy;
            Display.requirementsInfo.text =
                $"Requires: {Utils.formatBigValue((float)ec, " EC")}";
            Display.subnodesToggle.interactable = children.Count > 0;
        }

        void on_recycled(bool _success)
        {
            if(Display != null && Display.ui != null)
                Display.ui.reportPane.SetReport(Recycler.GetRecycleReport().ToArray());
            Update();
        }

        public void Recycle(bool discard_excess_resources, Action<bool> on_finished)
        {
            if(Recycler != null && !Recycler.IsRecycling)
            {
                on_finished += on_recycled;
                Recycler.Recycle(part, discard_excess_resources, on_finished);
            }
        }
    }

    internal class PartsRegistry : IEnumerable<RecyclablePart>
    {
        private readonly IRecycler recycler;
        private readonly List<uint> order = new List<uint>();

        private readonly Dictionary<uint, RecyclablePart> map =
            new Dictionary<uint, RecyclablePart>();

        public PartsRegistry(IRecycler recycler)
        {
            this.recycler = recycler;
        }

        public int Count => map.Count;

        public virtual RecyclablePart Add(Part part)
        {
            if(!map.ContainsKey(part.persistentId))
            {
                var rp = new RecyclablePart(part, recycler);
                order.Add(rp.ID);
                map[rp.ID] = rp;
                return rp;
            }
            return null;
        }

        public virtual bool Remove(uint part_id)
        {
            if(map.Remove(part_id))
            {
                order.Remove(part_id);
                return true;
            }
            return false;
        }

        public virtual void Clear()
        {
            map.Clear();
            order.Clear();
        }

        public bool Contains(Part part) => Contains(part.persistentId);

        public bool Contains(uint part_id) => map.ContainsKey(part_id);

        public bool TryGetValue(uint part_id, out RecyclablePart part) =>
            map.TryGetValue(part_id, out part);

        public void Sync(
            IEnumerable<Part> parts,
            Action<RecyclablePart> on_add = null,
            Action<uint> on_remove = null
        )
        {
            var ids = new HashSet<uint>();
            foreach(var part in parts)
            {
                var pid = part.persistentId;
                ids.Add(pid);
                if(!map.ContainsKey(pid))
                {
                    var rp = Add(part);
                    if(rp != null)
                        on_add?.Invoke(rp);
                }
            }
            foreach(var pid in map.Keys.ToList())
            {
                if(!ids.Contains(pid))
                {
                    if(Remove(pid))
                        on_remove?.Invoke(pid);
                }
            }
        }

        public void ForEach(Action<RecyclablePart> action)
        {
            foreach(var rp in map.Values)
                action(rp);
        }

        public IEnumerator<RecyclablePart> GetEnumerator()
        {
            foreach(var pid in order)
                yield return map[pid];
        }

        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }

    class ChildPartsRegistry : PartsRegistry
    {
        private readonly RecyclablePart parent;

        public ChildPartsRegistry(RecyclablePart parent) : base(parent.Recycler)
        {
            this.parent = parent;
        }

        public override RecyclablePart Add(Part part)
        {
            var rp = base.Add(part);
            if(rp != null && parent.Display != null)
                parent.Display.RefreshSubnodes();
            return rp;
        }

        public override bool Remove(uint part_id)
        {
            if(base.Remove(part_id))
            {
                if(parent.Display != null)
                    parent.Display.RefreshSubnodes();
                return true;
            }
            return false;
        }

        public override void Clear()
        {
            base.Clear();
            if(parent.Display != null)
                parent.Display.subnodesToggle.isOn = false;
        }
    }
}