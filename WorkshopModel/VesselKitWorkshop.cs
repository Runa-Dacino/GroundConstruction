﻿//   VesselKitWorkshop.cs
//
//  Author:
//       Allis Tauri <allista@gmail.com>
//
//  Copyright (c) 2017 Allis Tauri

using System;
using System.Linq;
using System.Collections.Generic;
using UnityEngine;
using AT_Utils;
using AT_Utils.UI;

namespace GroundConstruction
{
    public abstract class VesselKitWorkshop<KitInfo> : WorkshopBase<KitInfo, ConstructionSkill>
        where KitInfo : VesselKitInfo, new()
    {
        protected double serve_requirements(double work)
        {
            var req = CurrentTask.Kit.RequirementsForWork(work);
            //this.Log(req.ToString());//debug
            double have_res = 0, have_ec = 0, used_res = 0, used_ec = 0;
            //get required resource
            if(req.resource_amount > 0)
            {
                have_res = part.RequestResource(req.resource.id, req.resource_amount);
                if(have_res.Equals(0))
                {
                    Utils.Message("Not enough {0}. The work on {1} was put on hold.",
                        req.resource.name,
                        CurrentTask.Name);
                    work = 0;
                    goto end;
                }
            }
            //get required EC
            if(req.energy > 0)
            {
                have_ec = part.RequestResource(Utils.ElectricCharge.id, req.energy);
                if(have_ec / req.energy < GLB.WorkshopShutdownThreshold)
                {
                    Utils.Message("Not enough energy. The work on {0} was put on hold.",
                        CurrentTask.Name);
                    work = 0;
                    goto end;
                }
            }
            //correct the amount of work we can do and of resources we need
            var frac = 1.0;
            if(req.resource_amount > 0)
                frac = have_res / req.resource_amount;
            if(req.energy > 0)
                frac = Math.Min(frac, have_ec / req.energy);
            used_res = req.resource_amount * frac;
            used_ec = req.energy * frac;
            work = req.work * frac;
            //return unused resources
            end:
            if(used_res < have_res)
                part.RequestResource(req.resource.id, used_res - have_res);
            if(used_ec < have_ec)
                part.RequestResource(Utils.ElectricCharge.id, used_ec - have_ec);
            if(work.Equals(0))
                stop();
            return work;
        }

        protected virtual void on_task_complete(KitInfo task) => task.Controllable?.EnableControls();

        protected override double do_some_work(double available_work)
        {
            var work = serve_requirements(available_work);
//            this.Log($"do_some_work can do work: {work}/{available_work}, eta {CurrentTask.Kit.CurrentTaskETA}"); //debug
            if(work > 0)
            {
                CurrentTask.Kit.DoSomeWork(work);
                if(CurrentTask.Complete)
                {
                    on_task_complete(CurrentTask);
                    start_next_item();
                }
            }
            return available_work - work;
        }

        protected override void on_stop(bool reset)
        {
            if(CurrentTask.Recheck())
                CurrentTask.Kit.CheckoutWorker(this);
            if(reset)
                reset_current_task();
        }

        protected override bool check_task(KitInfo task) => base.check_task(task) && check_host(task);

        protected virtual bool check_host(KitInfo task) => task.Container != null && task.Container.Valid;

        #region available kits
        protected List<KitInfo> unbuilt_kits = new List<KitInfo>();
        protected List<KitInfo> built_kits = new List<KitInfo>();

        protected HashSet<Guid> get_queued_ids() => new HashSet<Guid>(Queue.Select(k => k.ID));

        protected float dist2kit(VesselKitInfo kit) =>
            (kit.Kit.Host.vessel.transform.position - part.partTransform.position).magnitude;

        protected float current_task_distance_mod()
        {
            var dist = dist2kit(CurrentTask);
            if(dist > GLB.MaxDistanceToWorkshop)
                return 0;
            return Mathf.Lerp(1,
                GLB.MaxDistanceEfficiency,
                Mathf.Max((dist - GLB.MinDistanceToWorkshop) / GLB.MaxDistanceToWorkshop, 0));
        }

        protected virtual void sort_task(KitInfo task)
        {
            if(check_task(task))
            {
                if(!task.Complete)
                    unbuilt_kits.Add(task);
                else
                    built_kits.Add(task);
            }
        }

        protected virtual void update_kits()
        {
            unbuilt_kits.Clear();
            built_kits.Clear();
        }
        #endregion

        #region implemented abstract members of WorkshopBase
        protected override void set_highlighted_task(KitInfo task)
        {
            if(task == null)
                set_highlighted_part(null);
            else if(task.Valid)
                set_highlighted_part(task.Kit.Host.part);
        }

        protected override void update_ui_data()
        {
            base.update_ui_data();
            if(FlightGlobals.ready)
                update_kits();
        }

        protected override void update_ETA()
        {
            update_workforce();
            var lastEndUT = EndUT;
            if(EffectiveWorkforce > 0)
            {
                if(LastUpdateTime < 0)
                    LastUpdateTime = Planetarium.GetUniversalTime();
                CurrentTask.Kit.CheckinWorker(this);
                var ETA = CurrentTask.Kit.CurrentTaskETA;
                if(ETA > 0)
                {
                    var time = Planetarium.GetUniversalTime();
                    EndUT = time + ETA;
                    ETA_Display = $"Time left: {Utils.formatTimeDelta(ETA)}";
                }
            }
            else
                EndUT = -1;
            if(EndUT < 0)
                ETA_Display = "Stalled...";
            if(Math.Abs(EndUT - lastEndUT) > 1)
                checkin();
        }

        protected override void draw()
        {
            Utils.LockIfMouseOver(LockName, WindowPos);
            WindowPos = GUILayout.Window(GetInstanceID(),
                    WindowPos,
                    main_window,
                    part.partInfo.title,
                    GUILayout.Width(width),
                    GUILayout.Height(height))
                .clampToScreen();
        }
        #endregion

        #region GUI
        protected Vector2 built_scroll = Vector2.zero;
        protected abstract void built_kits_pane();

        protected virtual void info_pane()
        {
            GUILayout.BeginHorizontal();
            if(GUILayout.Button(dismissCrewButton,
                Styles.danger_button,
                GUILayout.ExpandWidth(false)))
                do_crew_transfer = -1;
            GUILayout.Label(
                $"<color=silver>Workforce:</color> <b>{workforce:F1}</b>/{max_workforce:F1} SK",
                Styles.boxed_label,
                GUILayout.ExpandWidth(true));
            if(GUILayout.Button(getCrewButton,
                Styles.enabled_button,
                GUILayout.ExpandWidth(false)))
                do_crew_transfer = 1;
            GUILayout.EndHorizontal();
        }

        protected Vector2 unbuilt_scroll = Vector2.zero;
        protected abstract void unbuilt_kits_pane();

        protected void current_task_pane()
        {
            GUILayout.BeginVertical();
            GUILayout.BeginHorizontal();
            GUILayout.Label(Colors.Active.Tag("<b>Kit:</b>"),
                Styles.boxed_label,
                GUILayout.Width(40),
                GUILayout.ExpandHeight(true));
            draw_task(CurrentTask);
            GUILayout.EndHorizontal();
            GUILayout.BeginHorizontal();
            GUILayout.Label(Colors.Active.Tag("<b>Part:</b>"),
                Styles.boxed_label,
                GUILayout.Width(40),
                GUILayout.ExpandHeight(true));
            CurrentTask.DrawCurrentPart();
            GUILayout.EndHorizontal();
            GUILayout.EndVertical();
        }

        protected void construction_pane()
        {
            if(CurrentTask.Valid)
            {
                GUILayout.BeginVertical(Styles.white);
                GUILayout.Label(
                    Working
                        ? Colors.Active.Tag("<b>Working on</b>")
                        : Colors.Inactive.Tag("<b>Paused</b>"),
                    Styles.boxed_label,
                    GUILayout.ExpandWidth(true));
                current_task_pane();
                if(Working)
                {
                    GUILayout.BeginHorizontal();
                    GUILayout.Label(ETA_Display, Styles.boxed_label, GUILayout.ExpandWidth(true));
                    if(EndUT > 0
                       && TimeWarp.fetch != null
                       && GUILayout.Button(ProtoWorkshop.WarpToButton,
                           Styles.enabled_button,
                           GUILayout.ExpandWidth(false)))
                        TimeWarp.fetch.WarpTo(EndUT);
                    GUILayout.EndHorizontal();
                }
                GUILayout.EndVertical();
            }
            if(CurrentTask.Valid || Queue.Count > 0)
            {
                GUILayout.BeginHorizontal();
                if(Utils.ButtonSwitch("Pause Construction",
                    "Start Construction",
                    ref Working,
                    "Start, Pause or Resume construction",
                    GUILayout.ExpandWidth(true)))
                {
                    if(Working && can_construct())
                        start();
                    else
                        stop();
                }
                if(Working)
                {
                    if(GUILayout.Button(new GUIContent("Stop",
                            "Stop construction and move the kit back to the Queue"),
                        Styles.danger_button,
                        GUILayout.ExpandWidth(false)))
                    {
                        Queue.Enqueue(CurrentTask);
                        stop(true);
                    }
                }
                else
                    GUILayout.Label(new GUIContent("Stop",
                            "Stop construction and move the kit back to the Queue"),
                        Styles.inactive_button,
                        GUILayout.ExpandWidth(false));
                GUILayout.EndHorizontal();
            }
        }

        private Vector2 resources_scroll = Vector2.zero;

        protected virtual void resources_pane()
        {
            if(selected_task == null)
                return;
            if(!check_task(selected_task))
                selected_task = null;
            else if(selected_task.Kit.AdditionalResources.Count > 0)
            {
                GUILayout.BeginHorizontal();
                GUILayout.Label(string.Format("Additional resources required for <b>{0}</b>",
                        selected_task.Name),
                    Styles.label,
                    GUILayout.ExpandWidth(true));
                GUILayout.EndHorizontal();
                var h = Math.Max(selected_task.Kit.AdditionalResources.Count, 3) * 26;
                resources_scroll = GUILayout.BeginScrollView(resources_scroll,
                    GUILayout.Height(h));
                selected_task.Kit.AdditionalResources.Draw();
                GUILayout.EndScrollView();
            }
        }

        private void kit_configuration()
        {
            var cfg = selected_task?.Configurator;
            if(cfg != null && cfg.IsConfigurable)
            {
                GUILayout.BeginVertical();
                GUILayout.Label($"Construction options for <b>{selected_task.Name}</b>",
                    Styles.label,
                    GUILayout.ExpandWidth(true));
                cfg.DrawOptions();
                GUILayout.EndVertical();
            }
        }

        protected virtual void buttons_pane()
        {
            GUILayout.BeginHorizontal();
            GUILayout.FlexibleSpace();
            if(GUILayout.Button("Close", Styles.close_button, GUILayout.ExpandWidth(false)))
                show_window = false;
            GUILayout.EndHorizontal();
        }

        protected virtual void draw_panes()
        {
            info_pane();
            unbuilt_kits_pane();
            queue_pane();
            construction_pane();
            built_kits_pane();
            kit_configuration();
            resources_pane();
            buttons_pane();
        }

        protected virtual void main_window(int WindowID)
        {
            GUILayout.BeginVertical();
            draw_panes();
            GUILayout.EndVertical();
            GUIWindowBase.TooltipsAndDragWindow();
        }
        #endregion
    }
}
