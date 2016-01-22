﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using Smooth.Pools;
using UnityEngine;
using UnityToolbag;
using Random = System.Random;

namespace MuMech
{
    public class MechJebModuleLandingPredictions : ComputerModule
    {
        //publicly available output:
        //Call this function and use the returned object in case this.result changes while you
        //are doing your calculations:
        // TODO does calculating the ASL at this point work? We are doing this here to avoid making a call in the CelestialBody object from the reentrysimulation thread
        public ReentrySimulation.Result Result
        {
            get
            {
                if (result != null)
                {
                    if (result.body != null)
                    {
                        result.endASL = result.body.TerrainAltitude(result.endPosition.latitude, result.endPosition.longitude);
                    
                    
                        simDragScalar = result.prediction.firstDrag;
                        simLiftScalar = result.prediction.firstLift;
                        simDynamicPressurePa = result.prediction.dynamicPressurekPa * 1000;
                        simMach = result.prediction.mach;
                        simSpeedOfSound = result.prediction.speedOfSound;

                        //if (result.debugLog != "")
                        //{
                        //
                        //    MechJebCore.print("Now".PadLeft(8)
                        //               + " Alt:" + vesselState.altitudeASL.ToString("F0").PadLeft(6)
                        //               + " Vel:" + vesselState.speedOrbital.ToString("F2").PadLeft(8)
                        //               + " AirVel:" + vesselState.speedSurface.ToString("F2").PadLeft(8)
                        //               + " SoS:" +  vesselState.speedOfSound.ToString("F2").PadLeft(6)
                        //               + " mach:" + vesselState.mach.ToString("F2").PadLeft(6)
                        //               + " dynP:" + (vesselState.dynamicPressure / 1000).ToString("F5").PadLeft(9)
                        //               + " Temp:" + vessel.atmosphericTemperature.ToString("F2").PadLeft(8)
                        //               + " Lat:" + vesselState.latitude.ToString("F2").PadLeft(6));
                        //
                        //
                        //    MechJebCore.print(result.debugLog);
                        //    result.debugLog = "";
                        //
                        //    Vector3 scaledPos = ScaledSpace.LocalToScaledSpace(vessel.transform.position);
                        //    Vector3 sunVector = (FlightGlobals.Bodies[0].scaledBody.transform.position - scaledPos).normalized;
                        //
                        //    float sunDot = Vector3.Dot(sunVector, vessel.upAxis);
                        //    float sunAxialDot = Vector3.Dot(sunVector, vessel.mainBody.bodyTransform.up);
                        //    MechJebCore.print("sunDot " + sunDot.ToString("F3") + " sunAxialDot " + sunAxialDot.ToString("F3") + " " + PhysicsGlobals.DragUsesAcceleration);
                        //
                        //}


                    }
                }
                return result;
            }
        }
        
        public ReentrySimulation.Result GetErrorResult() 
        {
            if (null != errorResult)
            {
                if (null != errorResult.body)
                {
                    errorResult.endASL = errorResult.body.TerrainAltitude(errorResult.endPosition.latitude, errorResult.endPosition.longitude);
                }
            }
            return errorResult;
        }

        [ValueInfoItem("Sim Drag Scalar", InfoItem.Category.Vessel, format = ValueInfoItem.SI, units = "m/s²")]
        public double simDragScalar;

        [ValueInfoItem("Sim Lift Scalar", InfoItem.Category.Vessel, format = ValueInfoItem.SI, units = "m/s²")]
        public double simLiftScalar;

        [ValueInfoItem("Sim DynaPressPa", InfoItem.Category.Vessel, format = ValueInfoItem.SI, units = "pa")]
        public double simDynamicPressurePa;

        [ValueInfoItem("Sim simMach", InfoItem.Category.Vessel, format = "F2")]
        public double simMach;

        [ValueInfoItem("Sim SpdOfSnd", InfoItem.Category.Vessel, format = ValueInfoItem.SI, units = "m/s")]
        public double simSpeedOfSound;

        //inputs:
        [Persistent(pass = (int)Pass.Global)]
        public bool makeAerobrakeNodes = false;

        [Persistent(pass = (int)Pass.Global)]
        public bool showTrajectory = false;

        [Persistent(pass = (int)Pass.Global)]
        public bool worldTrajectory = true;

        [Persistent(pass = (int)Pass.Global)]
        public bool camTrajectory = false;

        public bool deployChutes = false;        
        public int limitChutesStage = 0;

        //simulation inputs:
        public double decelEndAltitudeASL = 0; // The altitude at which we need to have killed velocity - NOTE that this is not the same as the height of the predicted landing site.
        public IDescentSpeedPolicy descentSpeedPolicy = null; //simulate this descent speed policy
        public double parachuteSemiDeployMultiplier = 3; // this will get updated by the autopilot.
        public bool runErrorSimulations = false; // This will be set by the autopilot to turn error simulations on or off.

        //internal data:
      
        protected bool errorSimulationRunning = false; // the predictor can run two types of simulation - 1) simulations of the current situation. 2) simulations of the current situation with deliberate error introduced into the parachute multiplier to aid the statistical analysis of these results.
        protected System.Diagnostics.Stopwatch errorStopwatch = new System.Diagnostics.Stopwatch();
        protected long millisecondsBetweenErrorSimulations;

        private bool simulationRunning = false;
        public bool SimulationRunning
        {
            get { return simulationRunning; }
        }
        protected System.Diagnostics.Stopwatch stopwatch = new System.Diagnostics.Stopwatch();
        public double SimulationRunningTime
        {
            get { return simulationRunning ? stopwatch.ElapsedMilliseconds / 1000d : 0; }
        }
        protected long millisecondsBetweenSimulations;

        protected ReentrySimulation.Result result = null;
        protected ReentrySimulation.Result errorResult = null;

        public ManeuverNode aerobrakeNode = null;
        
        protected const int interationsPerSecond = 5; // the number of times that we want to try to run the simulation each second.
        protected double dt = 0.2; // the suggested dt for each timestep in the simulations. This will be adjusted depending on how long the simulations take to run.
        // TODO - decide if variable for fixed dt results in a more stable result
        protected bool variabledt = true; // Set this to true to allow the predictor to choose a dt based on how long each run is taking, and false to use a fixed dt.
        
        private readonly Queue<ReentrySimulation.Result> readyResults = new Queue<ReentrySimulation.Result>();

        private Random random;
        public double maxOrbits = 1;

        public override void OnStart(PartModule.StartState state)
        {
            random = new Random();
            if (state != PartModule.StartState.None && state != PartModule.StartState.Editor)
            {
                RenderingManager.AddToPostDrawQueue(1, DoMapView);
            }
        }

        public override void OnModuleEnabled()
        {
            TryStartSimulation(false);
        }

        public override void OnModuleDisabled()
        {
            stopwatch.Stop();
            stopwatch.Reset();

            errorStopwatch.Stop();
            errorStopwatch.Reset();
        }

        public override void OnFixedUpdate()
        {
            CheckForResult();

            TryStartSimulation(true);
        }

        private void TryStartSimulation(bool doErrorSim)
        {
            try
            {
                if (vessel.isActiveVessel && !vessel.LandedOrSplashed)
                {
                    // We should be running simulations periodically. If one is not running right now,
                    // check if enough time has passed since the last one to start a new one:
                    if (!simulationRunning && (stopwatch.ElapsedMilliseconds > millisecondsBetweenSimulations || !stopwatch.IsRunning))
                    {
                        stopwatch.Stop();
                        stopwatch.Reset();

                        StartSimulation(false);
                    }

                    // We also periodically run simulations containing deliberate errors if we have been asked to do so by the landing autopilot.
                    if (doErrorSim && this.runErrorSimulations && !errorSimulationRunning && (errorStopwatch.ElapsedMilliseconds >= millisecondsBetweenErrorSimulations || !errorStopwatch.IsRunning))
                    {
                        errorStopwatch.Stop();
                        errorStopwatch.Reset();

                        StartSimulation(true);
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.LogException(ex);
            }
        }

        public override void OnUpdate()
        {
            if (vessel.isActiveVessel)
            {
                MaintainAerobrakeNode();
            }
        }

        protected void StartSimulation(bool addParachuteError)
        {
            double altitudeOfPreviousPrediction = 0;
            double parachuteMultiplierForThisSimulation = this.parachuteSemiDeployMultiplier;
            if (addParachuteError)
            {
                errorSimulationRunning = true;
                errorStopwatch.Start(); //starts a timer that times how long the simulation takes
            }
            else
            {
                simulationRunning = true;
                stopwatch.Start(); //starts a timer that times how long the simulation takes
            }
            Orbit patch = GetReenteringPatch() ?? orbit;
            // Work out what the landing altitude was of the last prediction, and use that to pass into the next simulation
            if (result != null)
            {
                if (result.outcome == ReentrySimulation.Outcome.LANDED && result.body != null)
                {
                    altitudeOfPreviousPrediction = this.Result.endASL; // Note that we are caling GetResult here to force the it to calculate the endASL, if it has not already done this. It is not allowed to do this previously as we are only allowed to do it from this thread, not the reentry simulation thread.
                }
            }
            // Is this a simulation run with errors added? If so then add some error to the parachute multiple
            if (addParachuteError)
            {
                parachuteMultiplierForThisSimulation *= 1d + (random.Next(1000000) - 500000d) / 10000000d;
            }

            // The curves used for the sim are not thread safe so we need a copy used only by the thread
            ReentrySimulation.SimCurves simCurves = ReentrySimulation.SimCurves.Borrow(patch.referenceBody);

            SimulatedVessel simVessel = SimulatedVessel.Borrow(vessel, simCurves, patch.StartUT, deployChutes ? limitChutesStage : -1);
            ReentrySimulation sim = ReentrySimulation.Borrow(patch, patch.StartUT, simVessel, simCurves, descentSpeedPolicy, decelEndAltitudeASL, vesselState.limitedMaxThrustAccel, parachuteMultiplierForThisSimulation, altitudeOfPreviousPrediction, addParachuteError, dt, Time.fixedDeltaTime, maxOrbits);
            //MechJebCore.print("Sim ran with dt=" + dt.ToString("F3"));

            //Run the simulation in a separate thread
            ThreadPool.QueueUserWorkItem(RunSimulation, sim);
            //RunSimulation(sim);
        }

        private void RunSimulation(object o)
        {
            ReentrySimulation sim = (ReentrySimulation)o;
            try
            {
                ReentrySimulation.Result newResult = sim.RunSimulation();

                lock (readyResults)
                {
                    readyResults.Enqueue(newResult);
                }

                if (newResult.multiplierHasError)
                {
                    //see how long the simulation took
                    errorStopwatch.Stop();
                    long millisecondsToCompletion = errorStopwatch.ElapsedMilliseconds;

                    errorStopwatch.Reset();

                    //set the delay before the next simulation
                    millisecondsBetweenErrorSimulations = Math.Min(Math.Max(4 * millisecondsToCompletion, 400), 5);
                    // Note that we are going to run the simulations with error in less often that the real simulations

                    //start the stopwatch that will count off this delay
                    errorStopwatch.Start();
                    errorSimulationRunning = false;
                }
                else
                {
                    //see how long the simulation took
                    stopwatch.Stop();
                    long millisecondsToCompletion = stopwatch.ElapsedMilliseconds;
                    stopwatch.Reset();

                    //set the delay before the next simulation
                    millisecondsBetweenSimulations = Math.Min(Math.Max(2 * millisecondsToCompletion, 200), 5);
                    // Do not wait for too long before running another simulation, but also give the processor a rest. 

                    // How long should we set the max_dt to be in the future? Calculate for interationsPerSecond runs per second. If we do not enter the atmosphere, however do not do so as we will complete so quickly, it is not a good guide to how long the reentry simulation takes.
                    if (newResult.outcome == ReentrySimulation.Outcome.AEROBRAKED ||
                        newResult.outcome == ReentrySimulation.Outcome.LANDED)
                    {
                        if (this.variabledt)
                        {
                            dt = newResult.maxdt * (millisecondsToCompletion / 1000d) / (1d / (3d * interationsPerSecond));
                            // There is no point in having a dt that is smaller than the physics frame rate as we would be trying to be more precise than the game.
                            dt = Math.Max(dt, sim.min_dt);
                            // Set a sensible upper limit to dt as well. - in this case 10 seconds
                            dt = Math.Min(dt, 10);
                        }
                    }
                    
                    //Debug.Log("Result:" + this.result.outcome + " Time to run: " + millisecondsToCompletion + " millisecondsBetweenSimulations: " + millisecondsBetweenSimulations + " new dt: " + dt + " Time.fixedDeltaTime " + Time.fixedDeltaTime + "\n" + this.result.ToString()); // Note the endASL will be zero as it has not yet been calculated, and we are not allowed to calculate it from this thread :(

                    //start the stopwatch that will count off this delay
                    stopwatch.Start();
                    simulationRunning = false;
                }
            }
            catch (Exception ex)
            {
                //Debug.Log(string.Format("Exception in MechJebModuleLandingPredictions.RunSimulation\n{0}", ex.StackTrace));
                //Debug.LogException(ex);
                Dispatcher.InvokeAsync(() => Debug.LogException(ex));
            }
            finally
            {
                sim.Release();
            }
        }

        private void CheckForResult()
        {
            lock (readyResults)
            {
                while (readyResults.Count > 0)
                {
                    ReentrySimulation.Result newResult = readyResults.Dequeue();
                    
                    // If running the simulation resulted in an error then just ignore it.
                    if (newResult.outcome != ReentrySimulation.Outcome.ERROR)
                    {
                        if (newResult.multiplierHasError)
                        {
                            if (errorResult != null)
                                errorResult.Release();
                            errorResult = newResult;
                        }
                        else
                        {
                            if (result != null)
                                result.Release();
                            result = newResult;
                        }
                    }
                    else
                    {
                        if (newResult.exception != null)
                            print("Exception in the last simulation\n" + newResult.exception.Message + "\n" + newResult.exception.StackTrace);
                        newResult.Release();
                    }
                }
            }
        }

        protected Orbit GetReenteringPatch()
        {
            Orbit patch = orbit;

            int i = 0;

            do
            {
                i++;
                double reentryRadius = patch.referenceBody.Radius + patch.referenceBody.RealMaxAtmosphereAltitude();
                Orbit nextPatch = vessel.GetNextPatch(patch, aerobrakeNode);
                if (patch.PeR < reentryRadius)
                {
                    if (patch.Radius(patch.StartUT) < reentryRadius) return patch;

                    double reentryTime = patch.NextTimeOfRadius(patch.StartUT, reentryRadius);
                    if (patch.StartUT < reentryTime && (nextPatch == null || reentryTime < nextPatch.StartUT))
                    {
                        return patch;
                    }
                }

                patch = nextPatch;
            }
            while (patch != null);

            return null;
        }

        protected void MaintainAerobrakeNode()
        {
            if (makeAerobrakeNodes)
            {
                //Remove node after finishing aerobraking:
                if (aerobrakeNode != null && vessel.patchedConicSolver.maneuverNodes.Contains(aerobrakeNode))
                {
                    if (aerobrakeNode.UT < vesselState.time && vesselState.altitudeASL > mainBody.RealMaxAtmosphereAltitude())
                    {
                        vessel.patchedConicSolver.RemoveManeuverNode(aerobrakeNode);
                        aerobrakeNode = null;
                    }
                }

                //Update or create node if necessary:
                ReentrySimulation.Result r = Result;
                if (r != null && r.outcome == ReentrySimulation.Outcome.AEROBRAKED)
                {
                    //Compute the node dV:
                    Orbit preAerobrakeOrbit = GetReenteringPatch();

                    //Put the node at periapsis, unless we're past periapsis. In that case put the node at the current time.
                    double UT;
                    if (preAerobrakeOrbit == orbit &&
                        vesselState.altitudeASL < mainBody.RealMaxAtmosphereAltitude() && vesselState.speedVertical > 0)
                    {
                        UT = vesselState.time;
                    }
                    else
                    {
                        UT = preAerobrakeOrbit.NextPeriapsisTime(preAerobrakeOrbit.StartUT);
                    }

                    Orbit postAerobrakeOrbit = MuUtils.OrbitFromStateVectors(r.WorldAeroBrakePosition(), r.WorldAeroBrakeVelocity(), r.body, r.aeroBrakeUT);

                    Vector3d dV = OrbitalManeuverCalculator.DeltaVToChangeApoapsis(preAerobrakeOrbit, UT, postAerobrakeOrbit.ApR);

                    if (aerobrakeNode != null && vessel.patchedConicSolver.maneuverNodes.Contains(aerobrakeNode))
                    {
                        //update the existing node
                        Vector3d nodeDV = preAerobrakeOrbit.DeltaVToManeuverNodeCoordinates(UT, dV);
                        aerobrakeNode.OnGizmoUpdated(nodeDV, UT);
                    }
                    else
                    {
                        //place a new node
                        aerobrakeNode = vessel.PlaceManeuverNode(preAerobrakeOrbit, dV, UT);
                    }
                }
                else
                {
                    //no aerobraking, remove the node:
                    if (aerobrakeNode != null && vessel.patchedConicSolver.maneuverNodes.Contains(aerobrakeNode))
                    {
                        vessel.patchedConicSolver.RemoveManeuverNode(aerobrakeNode);
                    }
                }
            }
            else
            {
                //Remove aerobrake node when it is turned off:
                if (aerobrakeNode != null && vessel.patchedConicSolver.maneuverNodes.Contains(aerobrakeNode))
                {
                    vessel.patchedConicSolver.RemoveManeuverNode(aerobrakeNode);
                }
            }
        }

        void DoMapView()
        {
            if ((MapView.MapIsEnabled || camTrajectory) && vessel.isActiveVessel && this.enabled)
            {
                ReentrySimulation.Result drawnResult = Result;
                if (drawnResult != null)
                {
                    if (drawnResult.outcome == ReentrySimulation.Outcome.LANDED)
                        GLUtils.DrawMapViewGroundMarker(drawnResult.body, drawnResult.endPosition.latitude, drawnResult.endPosition.longitude, Color.blue, MapView.MapIsEnabled ? 60 : 1);

                    if (showTrajectory && drawnResult.outcome != ReentrySimulation.Outcome.ERROR && drawnResult.outcome != ReentrySimulation.Outcome.NO_REENTRY)
                    {
                        double interval = Math.Min((drawnResult.endUT - drawnResult.input_UT) / 100, 10);
                        //using (var list = drawnResult.WorldTrajectory(interval, worldTrajectory && MapView.MapIsEnabled))
                        using (var list = drawnResult.WorldTrajectory(interval, worldTrajectory))
                        {
                            GLUtils.DrawPath(drawnResult.body, list.value, Color.red);
                        }
                    }
                }
            }
        }

        public MechJebModuleLandingPredictions(MechJebCore core) : base(core) { }
    }
}
