﻿using Celeste.Mod.CelesteNet.Client.Entities;
using Celeste.Mod.CelesteNet.DataTypes;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Microsoft.Xna.Framework.Input;
using Monocle;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using MDraw = Monocle.Draw;

namespace Celeste.Mod.CelesteNet.Client.Components {
    public class CelesteNetChatComponent : CelesteNetGameComponent {

        protected float _Time;

        public float Scale => Settings.UIScale;

        protected Overlay _DummyOverlay = new PauseUpdateOverlay();

        public List<DataChat> Log = new List<DataChat>();
        public List<DataChat> LogSpecial = new List<DataChat>();
        public Dictionary<string, DataChat> Pending = new Dictionary<string, DataChat>();
        public string Typing = "";

        public ChatMode Mode => Active ? ChatMode.All : Settings.ShowNewMessages;

        public enum ChatMode {
            All,
            Special,
            Off
        }

        public List<string> Repeat = new List<string>() {
            ""
        };

        protected int _RepeatIndex = 0;
        public int RepeatIndex {
            get => _RepeatIndex;
            set {
                if (_RepeatIndex == value)
                    return;

                value = (value + Repeat.Count) % Repeat.Count;

                if (_RepeatIndex == 0 && value != 0)
                    Repeat[0] = Typing;
                Typing = Repeat[value];
                _RepeatIndex = value;
            }
        }

        protected bool _SceneWasPaused;
        protected int _ConsumeInput;
        protected bool _Active;
        public bool Active {
            get => _Active;
            set {
                if (_Active == value)
                    return;

                if (value) {
                    _SceneWasPaused = Engine.Scene.Paused;
                    Engine.Scene.Paused = true;
                    // If we're in a level, add a dummy overlay to prevent the pause menu from handling input.
                    if (Engine.Scene is Level level)
                        level.Overlay = _DummyOverlay;

                    _RepeatIndex = 0;
                    _Time = 0;
                    TextInput.OnInput += OnTextInput;

                } else {
                    Typing = "";
                    Engine.Scene.Paused = _SceneWasPaused;
                    _ConsumeInput = 2;
                    if (Engine.Scene is Level level && level.Overlay == _DummyOverlay)
                        level.Overlay = null;
                    TextInput.OnInput -= OnTextInput;
                }

                _Active = value;
            }
        }

        public CelesteNetChatComponent(CelesteNetClientContext context, Game game)
            : base(context, game) {

            UpdateOrder = 10000;
            DrawOrder = 10100;
        }

        public void Send(string text) {
            text = text?.Trim();
            if (string.IsNullOrEmpty(text))
                return;

            lock (Log) {
                if (Pending.ContainsKey(text))
                    return;
                DataChat msg = new DataChat {
                    Player = Client.PlayerInfo,
                    Text = text
                };
                Pending[text] = msg;
                Log.Add(msg);
                LogSpecial.Add(msg);
                Client.Send(msg);
            }
        }

        public void Handle(CelesteNetConnection con, DataChat msg) {
            lock (Log) {
                if (msg.Player != null && msg.Player.ID == Client.PlayerInfo?.ID && Pending.TryGetValue(msg.Text, out DataChat pending)) {
                    Pending.Remove(msg.Text);
                    Log.Remove(pending);
                    LogSpecial.Remove(pending);
                }

                int index = Log.FindLastIndex(other => other.ID == msg.ID);
                if (index != -1) {
                    Log[index] = msg;
                } else {
                    Log.Add(msg);
                }

                if (msg.Color != Color.White) {
                    index = LogSpecial.FindLastIndex(other => other.ID == msg.ID);
                    if (index != -1) {
                        LogSpecial[index] = msg;
                    } else {
                        LogSpecial.Add(msg);
                    }
                }
            }
        }

        public override void Update(GameTime gameTime) {
            base.Update(gameTime);

            _Time += Engine.RawDeltaTime;

            bool isRebinding = Engine.Scene == null ||
                Engine.Scene.Entities.FindFirst<KeyboardConfigUI>() != null ||
                Engine.Scene.Entities.FindFirst<ButtonConfigUI>() != null;

            if (!(Engine.Scene?.Paused ?? true) || isRebinding) {
                string typing = Typing;
                Active = false;
                Typing = typing;
            }

            if (!Active && !isRebinding && Settings.ButtonChat.Button.Pressed) {
                Active = true;

            } else if (Active) {
                Engine.Commands.Open = false;

                if (MInput.Keyboard.Pressed(Keys.Enter)) {
                    if (!string.IsNullOrWhiteSpace(Typing))
                        Repeat.Insert(1, Typing);
                    Send(Typing);
                    Active = false;

                } else if (MInput.Keyboard.Pressed(Keys.Down) && RepeatIndex > 0) {
                    RepeatIndex--;
                } else if (MInput.Keyboard.Pressed(Keys.Up) && RepeatIndex < Repeat.Count - 1) {
                    RepeatIndex++;

                } else if (Input.ESC.Pressed || Input.Pause.Pressed) {
                    Active = false;
                }
            }

            // Prevent menus from reacting to player input after exiting chat.
            if (_ConsumeInput > 0) {
                Input.MenuConfirm.ConsumeBuffer();
                Input.MenuConfirm.ConsumePress();
                Input.ESC.ConsumeBuffer();
                Input.ESC.ConsumePress();
                Input.Pause.ConsumeBuffer();
                Input.Pause.ConsumePress();
                _ConsumeInput--;
            }

        }

        public void OnTextInput(char c) {
            if (!Active)
                return;

            if (c == (char) 13) {
                // Enter - send.
                // Handled in Update.

            } else if (c == (char) 8) {
                // Backspace - trim.
                if (Typing.Length > 0)
                    Typing = Typing.Substring(0, Typing.Length - 1);
                _RepeatIndex = 0;
                _Time = 0;

            } else if (c == (char) 127) {
                // Delete - currenly not handled.

            } else if (!char.IsControl(c)) {
                // Any other character - append.
                Typing += c;
                _RepeatIndex = 0;
                _Time = 0;
            }
        }

        protected override void Render(GameTime gameTime, bool toBuffer) {
            float scale = Scale;
            Vector2 fontScale = Vector2.One * scale;

            if (Active) {
                Context.Blur.Rect(25f * scale, UI_HEIGHT - 125f * scale, UI_WIDTH - 50f * scale, 100f * scale, Color.Black * 0.8f);

                CelesteNetClientFont.Draw(
                    ">",
                    new Vector2(50f * scale, UI_HEIGHT - 105f * scale),
                    Vector2.Zero,
                    fontScale * new Vector2(0.5f, 1f),
                    Color.White * 0.5f
                );
                float offs = CelesteNetClientFont.Measure(">").X * scale;

                string text = Typing;
                CelesteNetClientFont.Draw(
                    text,
                    new Vector2(50f * scale + offs, UI_HEIGHT - 105f * scale),
                    Vector2.Zero,
                    fontScale,
                    Color.White
                );

                if (!Calc.BetweenInterval(_Time, 0.5f)) {
                    offs += CelesteNetClientFont.Measure(text).X * scale;
                    CelesteNetClientFont.Draw(
                        "_",
                        new Vector2(50f * scale + offs, UI_HEIGHT - 105f * scale),
                        Vector2.Zero,
                        fontScale,
                        Color.White * 0.5f
                    );
                }
            }

            lock (Log) {
                List<DataChat> log;
                switch (Mode) {
                    case ChatMode.All:
                    default:
                        log = Log;
                        break;

                    case ChatMode.Special:
                        log = LogSpecial;
                        break;

                    case ChatMode.Off:
                        log = Dummy<DataChat>.EmptyList;
                        break;
                }

                int count = log.Count;
                if (count > 0) {
                    DateTime now = DateTime.UtcNow;

                    float y = UI_HEIGHT - 50f * scale;
                    if (Active)
                        y -= 105f * scale;

                    float logLength = Settings.ChatLogLength;
                    for (int i = 0; i < count && i < logLength; i++) {
                        DataChat msg = log[count - 1 - i];

                        float alpha = 1f;
                        float delta = (float) (now - msg.ReceivedDate).TotalSeconds;
                        if (!Active && delta > 3f)
                            alpha = 1f - Ease.CubeIn(delta - 3f);
                        if (alpha <= 0f)
                            continue;

                        string text = msg.ToString();
                        logLength -= Math.Max(0, text.Count(c => c == '\n') - 1) * 0.75f;

                        int lineScaleTry = 0;
                        float lineScale = scale;
                        RetryLineScale:
                        Vector2 lineFontScale = Vector2.One * lineScale;

                        Vector2 size = CelesteNetClientFont.Measure(text) * lineFontScale;

                        if ((size.X + 100f * scale) > UI_WIDTH && lineScaleTry < 4) {
                            lineScaleTry++;
                            lineScale -= scale * 0.1f;
                            goto RetryLineScale;
                        }

                        float height = 50f * scale + size.Y;

                        y -= height;

                        Context.Blur.Rect(25f * scale, y, size.X + 50f * scale, height, Color.Black * 0.8f * alpha);
                        CelesteNetClientFont.Draw(
                            text,
                            new Vector2(50f * scale, y + 25f * scale),
                            Vector2.Zero,
                            lineFontScale,
                            msg.Color * alpha * (msg.ID == uint.MaxValue ? 0.8f : 1f)
                        );
                    }
                }
            }
        }

        protected override void Dispose(bool disposing) {
            if (Active)
                Active = false;

            base.Dispose(disposing);
        }

    }
}
