﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Xml.Linq;
using System.Xml.XPath;

namespace ForgottenRPG.Entity.Dialog {
    public class Dialog {
        public readonly string Name;
        public string Graphic => $"ui_avatar_{_graphicName}";
        public string Prompt => _dialogNodes[_currentNode].Prompt;
        public List<string> Replies => _dialogNodes[_currentNode].Replies;
        public event Action OnEnd;
        private readonly string _graphicName;
        private readonly Dictionary<int, DialogNode> _dialogNodes;
        private readonly IOpenDialog _dialogOpener;
        private int _currentNode = 0;

        public Dialog(string name, string graphic, IOpenDialog dialogOpener, Dictionary<int, DialogNode> dialogNodes) {
            Name = name;
            _graphicName = graphic;
            _dialogOpener = dialogOpener;
            _dialogNodes = dialogNodes;
        }

        public void StartDialog() {
            _dialogOpener.StartDialog(this);
        }

        public static Dialog FromXElement(string name, string graphic, XElement dialog, IOpenDialog dialogOpener) {
            Dictionary<int, DialogReply> replies = new Dictionary<int, DialogReply>();
            Dictionary<int, DialogNode> nodes = new Dictionary<int, DialogNode>();

            foreach (XElement node in dialog.XPathSelectElements("./Nodes/Node")) {
                int nodeId = int.Parse(node.Attribute("id")?.Value);
                string prompt = node.Elements("Prompt").FirstOrDefault()?.Value.Trim() ?? "";
                List<DialogReply> nodeReplies = new List<DialogReply>();

                foreach (XElement nodeReply in node.Elements("Reply")) {
                    int replyId;
                    if (!int.TryParse(nodeReply.Attribute("id")?.Value, out replyId))
                        throw new DialogException("Unspecified ID in node's reply reference");

                    if (!replies.ContainsKey(replyId)) {
                        XElement reply = dialog.XPathSelectElement($"./Replies/Reply[@id='{replyId}']");
                        replies.Add(replyId, LoadReply(reply));
                    }

                    nodeReplies.Add(replies[replyId]);
                }

                nodes.Add(nodeId, new DialogNode(nodeReplies, prompt));
            }

            return new Dialog(name, graphic, dialogOpener, nodes);
        }

        private static DialogReply LoadReply(XElement reply) {
            string prompt = reply.Elements("Prompt").FirstOrDefault()?.Value.Trim() ?? "";
            List<DialogAction> replyActions = new List<DialogAction>();

            foreach (XElement replyAction in reply.Elements("Action")) {
                switch (replyAction.Attribute("type")?.Value) {
                    case "changeNode":
                        replyActions.Add(new DialogActionChangeNode(int.Parse(replyAction.Attribute("id").Value)));
                        break;
                    case "endDiscussion":
                        replyActions.Add(new DialogActionEndDiscussion());
                        if (string.IsNullOrEmpty(prompt)) prompt = "End discussion";
                        break;
                    case "code":
                        replyActions.Add(new DialogActionCode(replyAction.Value.Split(',').Select(uint.Parse).ToList()));
                        break;
                }
            }

            return new DialogReply(prompt, replyActions);
        }

        public void ReplyActioned(int index) {
            _dialogNodes[_currentNode].ReplyActioned(this, index);
        }

        public void ChangeNode(int id) {
            _currentNode = id;
        }

        public void EndDialog() {
            OnEnd?.Invoke();
            _currentNode = 0;
            OnEnd = null;
        }
    }

    public class DialogException : Exception {
        public DialogException(string message) : base(message) { }
    }
}
