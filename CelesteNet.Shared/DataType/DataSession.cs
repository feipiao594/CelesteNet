﻿using Microsoft.Xna.Framework;
using Monocle;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Celeste.Mod.CelesteNet.DataTypes {
    public class DataSession : DataType<DataSession>, IDataRequestable<DataSessionRequest> {

        static DataSession() {
            DataID = "session";
        }

        public uint ID = uint.MaxValue;

        public bool InSession;

        public DataPartAudioState? Audio;
        public Vector2? RespawnPoint;
        public PlayerInventory Inventory;
        public HashSet<string>? Flags;
        public HashSet<string>? LevelFlags;
        public HashSet<EntityID>? Strawberries;
        public HashSet<EntityID>? DoNotLoad;
        public HashSet<EntityID>? Keys;
        public List<Session.Counter>? Counters;
        public string? FurthestSeenLevel;
        public string? StartCheckpoint;
        public string? ColorGrade;
        public bool[]? SummitGems;
        public bool FirstLevel;
        public bool Cassette;
        public bool HeartGem;
        public bool Dreaming;
        public bool GrabbedGolden;
        public bool HitCheckpoint;
        public float LightingAlphaAdd;
        public float BloomBaseAdd;
        public float DarkRoomAlpha;
        public long Time;
        public Session.CoreModes CoreMode;

        public override MetaType[] GenerateMeta(DataContext ctx)
            => new MetaType[] {
                new MetaRequestResponse(ID)
            };

        public override void FixupMeta(DataContext ctx) {
            ID = Get<MetaRequestResponse>(ctx);
        }

        public override void Read(DataContext ctx, BinaryReader reader) {
            InSession = reader.ReadBoolean();
            if (!InSession)
                return;

            byte bools;
            int count;

            if (reader.ReadBoolean()) {
                Audio = new DataPartAudioState();
                Audio.ReadAll(ctx, reader);
            }

            if (reader.ReadBoolean())
                RespawnPoint = new Vector2(reader.ReadSingle(), reader.ReadSingle());

            Inventory = new PlayerInventory();
            bools = reader.ReadByte();
            Inventory.Backpack = UnpackBool(bools, 0);
            Inventory.DreamDash = UnpackBool(bools, 1);
            Inventory.NoRefills = UnpackBool(bools, 2);
            Inventory.Dashes = reader.ReadByte();

            Flags = new HashSet<string>();
            count = reader.ReadByte();
            for (int i = 0; i < count; i++)
                Flags.Add(reader.ReadNullTerminatedString());

            LevelFlags = new HashSet<string>();
            count = reader.ReadByte();
            for (int i = 0; i < count; i++)
                LevelFlags.Add(reader.ReadNullTerminatedString());

            Strawberries = new HashSet<EntityID>();
            count = reader.ReadByte();
            for (int i = 0; i < count; i++)
                Strawberries.Add(new EntityID(reader.ReadNullTerminatedString(), reader.ReadInt32()));

            DoNotLoad = new HashSet<EntityID>();
            count = reader.ReadByte();
            for (int i = 0; i < count; i++)
                DoNotLoad.Add(new EntityID(reader.ReadNullTerminatedString(), reader.ReadInt32()));

            Keys = new HashSet<EntityID>();
            count = reader.ReadByte();
            for (int i = 0; i < count; i++)
                Keys.Add(new EntityID(reader.ReadNullTerminatedString(), reader.ReadInt32()));

            Counters = new List<Session.Counter>();
            count = reader.ReadByte();
            for (int i = 0; i < count; i++)
                Counters.Add(new Session.Counter {
                    Key = reader.ReadNullTerminatedString(),
                    Value = reader.ReadInt32()
                });

            FurthestSeenLevel = reader.ReadNullTerminatedString().Nullify();
            StartCheckpoint = reader.ReadNullTerminatedString().Nullify();
            ColorGrade = reader.ReadNullTerminatedString().Nullify();

            count = reader.ReadByte();
            SummitGems = new bool[count];
            for (int i = 0; i < count; i++) {
                if ((i % 8) == 0)
                    bools = reader.ReadByte();
                SummitGems[i] = UnpackBool(bools, i % 8);
            }

            bools = reader.ReadByte();
            FirstLevel = UnpackBool(bools, 0);
            Cassette = UnpackBool(bools, 1);
            HeartGem = UnpackBool(bools, 2);
            Dreaming = UnpackBool(bools, 3);
            GrabbedGolden = UnpackBool(bools, 4);
            HitCheckpoint = UnpackBool(bools, 5);

            LightingAlphaAdd = reader.ReadSingle();
            BloomBaseAdd = reader.ReadSingle();
            DarkRoomAlpha = reader.ReadSingle();

            Time = reader.ReadInt64();

            CoreMode = (Session.CoreModes) reader.ReadByte();
        }

        public override void Write(DataContext ctx, BinaryWriter writer) {
            if (!InSession) {
                writer.Write(false);
                return;
            }

            writer.Write(true);

#pragma warning disable CS8602 // These should be null if InSession is true. If they're null, a NRE is appropriate.

            byte bools;

            if (Audio != null) {
                writer.Write(true);
                Audio.WriteAll(ctx, writer);
            } else {
                writer.Write(false);
            }

            if (RespawnPoint != null) {
                writer.Write(true);
                writer.Write(RespawnPoint.Value.X);
                writer.Write(RespawnPoint.Value.Y);
            } else {
                writer.Write(false);
            }

            writer.Write(PackBools(Inventory.Backpack, Inventory.DreamDash, Inventory.NoRefills));
            writer.Write((byte) Inventory.Dashes);

            writer.Write((byte) Flags.Count);
            foreach (string value in Flags)
                writer.WriteNullTerminatedString(value);

            writer.Write((byte) LevelFlags.Count);
            foreach (string value in LevelFlags)
                writer.WriteNullTerminatedString(value);

            writer.Write((byte) Strawberries.Count);
            foreach (EntityID value in Strawberries) {
                writer.WriteNullTerminatedString(value.Level);
                writer.Write(value.ID);
            }

            writer.Write((byte) DoNotLoad.Count);
            foreach (EntityID value in DoNotLoad) {
                writer.WriteNullTerminatedString(value.Level);
                writer.Write(value.ID);
            }

            writer.Write((byte) Keys.Count);
            foreach (EntityID value in Keys) {
                writer.WriteNullTerminatedString(value.Level);
                writer.Write(value.ID);
            }

            writer.Write((byte) Counters.Count);
            foreach (Session.Counter value in Counters) {
                writer.WriteNullTerminatedString(value.Key);
                writer.Write(value.Value);
            }

            writer.WriteNullTerminatedString(FurthestSeenLevel);
            writer.WriteNullTerminatedString(StartCheckpoint);
            writer.WriteNullTerminatedString(ColorGrade);

            writer.Write((byte) SummitGems.Length);
            bools = 0;
            for (int i = 0; i < SummitGems.Length; i++) {
                bools = PackBool(bools, i % 8, SummitGems[i]);
                if (((i + 1) % 8) == 0) {
                    writer.Write(bools);
                    bools = 0;
                }
            }
            if (SummitGems.Length % 8 != 0)
                writer.Write(bools);

            writer.Write(PackBools(FirstLevel, Cassette, HeartGem, Dreaming, GrabbedGolden, HitCheckpoint));

            writer.Write(LightingAlphaAdd);
            writer.Write(BloomBaseAdd);
            writer.Write(DarkRoomAlpha);

            writer.Write(Time);

            writer.Write((byte) CoreMode);
#pragma warning restore CS8602
        }

    }
}
