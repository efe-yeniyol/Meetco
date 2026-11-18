import Foundation

/// Builds the prompts and JSON schemas for every assistant request.
enum PromptBuilder {
    // MARK: Messages

    /// - Parameter inlineSchema: describe the JSON schema in the prompt, for endpoints without strict structured output.
    static func messages(for request: AssistantRequest, inlineSchema: Bool = false) -> [ChatMessage] {
        var systemText = systemPrompt(for: request)
        if inlineSchema, let schema = schemaInstructions(for: request.kind) { systemText += "\n" + schema }
        let system = ChatMessage.system(systemText)
        let userText = userPrompt(for: request)
        if let screen = request.screenContext {
            return [system, .user(parts: [
                .text(userText),
                .text("A screenshot of the user's screen is attached (for example shared slides, a document or meeting chat). Use it only if it is relevant."),
                .jpeg(screen.jpegData, detail: "auto"),
            ])]
        }
        return [system, .user(userText)]
    }

    static func systemPrompt(for request: AssistantRequest) -> String {
        let preferences = request.preferences
        let isSpoken = request.kind == .answer || request.kind == .whatToSay
        let shared = """
        You are Meetco, a real-time meeting copilot that sits beside the user during a live call.
        You read a rolling, automatically generated transcript and help the user respond.

        Transcript conventions:
        - "Participant" lines are other people in the meeting (captured from the computer's audio output).
        - "You" lines are the user's own microphone.
        - Speech recognition can contain mistakes; infer the intended meaning sensibly.

        Rules:
        - Use your own knowledge freely for anything that is general knowledge: how things work, definitions, recipes, techniques, technical concepts, best practices, well-known facts. Give the real content with concrete specifics (ingredients and typical amounts, steps, names, numbers that are commonly known).
        - Never invent facts about the user or their business: their personal history, employers, prices, dates, commitments, internal numbers or product capabilities must come from the transcript or the user's notes. Only when an answer truly depends on such private details, give an honest answer that offers to confirm or follow up.
        - Style: \(preferences.style.promptDescription)
        - Language: \(languageRule(preferences.responseLanguage))
        """
        guard isSpoken else {
            return shared + "\n" + """
            - Explanation: \(explanationRule(preferences.explanationLanguage))
            - Always respond with a single JSON object that matches the requested schema.
            """
        }
        return shared + "\n" + """
        - Do not repeat earlier suggestions verbatim; improve on them or add new substance.
        - Use the whole transcript to understand what is being talked about, so that short or vague questions ("and the second one?", "how long?") are answered in context.

        Substance — the answer must actually answer:
        - Answer the real request behind the words. "Do you know X?", "Have you heard of X?", "Can you explain X?", "Are you familiar with X?" are requests to deliver X itself: confirm in two or three words, then immediately give the content (for a recipe: the ingredients with amounts, then the steps; for a concept: the explanation; for a how-to: the steps in order; for an opinion: the opinion and the reason).
        - A reply that only says "Yes, I know that" or only restates the question is a failed answer. Someone who heard nothing but your answer should have learned the thing that was asked.
        - Put the most important information first so the user can already start speaking, then add the details.

        Voice — you are writing the user's own words:
        - Write exactly what the user will say, as the user, in the first person ("I", and "we" for their team or company). The user reads your text aloud word for word while you are still writing it.
        - Never talk to the user, never describe an answer ("You could say…", "A good answer would be…"), never mention being an AI, an assistant or Meetco.
        - Sound like a real person speaking in a meeting, not like written text: everyday words, contractions, short sentences that are easy to say in one breath. No bullet points, no markdown, no headings, no quotation marks around the answer.
        - No filler openers such as "Great question", "Certainly" or "Thank you for asking". The first words must already be the answer.
        - Match how the user talks in their own "You" lines (formality, vocabulary, level of detail) when there are any.
        - Treat the user's notes as the user's own background and experience and speak from them naturally ("In my last role I…").
        - When asked about the user's own work or experience, answer confidently and concretely with a clear structure (for example a brief situation → action → result), using only facts from the user's notes; otherwise give a credible, general answer without inventing specific employers, numbers or dates.

        Output format — plain text only, never JSON, no labels:
        - Start immediately with the words to say. Speed matters: the user is live on a call and waiting.
        \(spokenExplanationRule(preferences.explanationLanguage))
        """
    }

    static func userPrompt(for request: AssistantRequest) -> String {
        var sections: [String] = []

        let context = request.preferences.userContext.trimmingCharacters(in: .whitespacesAndNewlines)
        sections.append("## About the user\n" + (context.isEmpty ? "No additional information provided." : context))
        sections.append("## Meeting topic so far\n" + (request.meetingTopic?.isEmpty == false ? request.meetingTopic! : "Not identified yet."))
        sections.append("## Transcript (oldest first)\n" + formattedTranscript(request.transcript))

        if !request.previousAnswers.isEmpty {
            sections.append("## Earlier suggestions (avoid repeating)\n" + request.previousAnswers.prefix(5).map { "- \($0)" }.joined(separator: "\n"))
        }

        sections.append("## Task\n" + task(for: request))
        return sections.joined(separator: "\n\n")
    }

    static func formattedTranscript(_ lines: [TranscriptLine]) -> String {
        guard !lines.isEmpty else { return "(The transcript is empty so far.)" }
        let formatter = DateFormatter()
        formatter.dateFormat = "HH:mm:ss"
        return lines.map { line in
            let language = line.languageCode.map { " [\($0)]" } ?? ""
            return "[\(formatter.string(from: line.timestamp))] \(line.speaker.promptLabel)\(language): \(line.text)"
        }.joined(separator: "\n")
    }

    private static func task(for request: AssistantRequest) -> String {
        switch request.kind {
        case .answer:
            if let question = request.focusQuestion, request.focusIsExplicit {
                return """
                The user wants help answering this question from the meeting: "\(question)"
                Write the words the user should say in reply.
                """
            }
            let hint = request.focusQuestion.map { "\nThe most recent likely question is: \"\($0)\"" } ?? ""
            return """
            Identify the most recent meaningful question or request that a Participant addressed to the user (or to the user's side), focusing on the last few Participant lines.\(hint)
            If there is such a question, write the words the user should say in reply.
            If there is nothing the user is expected to answer (small talk, rhetorical, already answered by the user, or a question between other participants), output exactly \(PlainAnswer.noQuestionMarker) and nothing else.
            """
        case .whatToSay:
            return """
            Suggest what the user should say next given the current state of the conversation.
            If a Participant question is still waiting for the user's answer, answer it.
            Otherwise write a natural contribution, in the user's own voice, that moves the conversation forward.
            """
        case .recap:
            return """
            Summarize the meeting so far for the user: a short summary, key points, action items (with owners when stated) and open questions.
            Only include what was actually said. Write the recap in \(recapLanguage(request.preferences)).
            """
        case .questionsToAsk:
            return """
            Suggest 3 to 5 smart, specific questions the user could ask the other participants next, based on the conversation so far.
            Write them in the language the participants are speaking (or \(request.preferences.responseLanguage.languageName ?? "the conversation language") if set). Keep each under 25 words.
            """
        }
    }

    private static func languageRule(_ language: ResponseLanguage) -> String {
        if let name = language.languageName {
            return "Always write suggested answers in \(name)."
        }
        return "Write the suggested answer in the language the Participant used for the question. If the conversation switches languages, follow the most recent Participant language."
    }

    private static func explanationRule(_ language: ExplanationLanguage) -> String {
        guard let name = language.languageName else {
            return "Set \"explanation\" to an empty string."
        }
        return "If the suggested answer is not in \(name), write \"explanation\" in \(name): a short translation and explanation of the suggested answer so the user understands exactly what they will say. If the answer is already in \(name), set \"explanation\" to an empty string."
    }

    /// Explanation instructions for the plain-text answer format.
    private static func spokenExplanationRule(_ language: ExplanationLanguage) -> String {
        guard let name = language.languageName else {
            return "- Write nothing after the answer: no explanation, no notes."
        }
        return """
        - If the answer is not in \(name): after the answer, write a line containing only \(PlainAnswer.explanationDelimiter) and then a short \(name) translation of the answer, so the user understands exactly what they are saying. If the answer is already in \(name), write nothing after it.
        """
    }

    private static func recapLanguage(_ preferences: AssistantPreferences) -> String {
        preferences.explanationLanguage.languageName ?? preferences.responseLanguage.languageName ?? "the main language of the meeting"
    }

    // MARK: Schemas

    /// The schema spelled out in the prompt. Nil for spoken answers, which are plain text.
    static func schemaInstructions(for kind: Suggestion.Kind) -> String? {
        guard kind != .answer && kind != .whatToSay else { return nil }
        return """
        - Output only the JSON object: no markdown, no code fences, no text before or after it. Include every property, in this order, as described by this JSON Schema:
        \(schema(for: kind).serialized())
        """
    }

    static func schemaName(for kind: Suggestion.Kind) -> String {
        switch kind {
        case .answer, .whatToSay: "meeting_answer"
        case .recap: "meeting_recap"
        case .questionsToAsk: "meeting_questions"
        }
    }

    /// Field order matters: the model writes fields in schema order, and the UI streams
    /// `question` and `suggested_answer` as soon as they start arriving.
    static func schema(for kind: Suggestion.Kind) -> JSONValue {
        switch kind {
        case .answer, .whatToSay:
            return object([
                "question_detected": ["type": "boolean", "description": "Whether a question for the user was found."],
                "question": ["type": "string", "description": "The question being answered, cleaned up. Empty if none."],
                "suggested_answer": ["type": "string", "description": "What the user can say out loud."],
                "explanation": ["type": "string", "description": "Explanation in the explanation language, or empty."],
                "language": ["type": "string", "description": "Language of suggested_answer, e.g. English."],
                "directed_at_user": ["type": "boolean"],
                "needs_more_info": ["type": "boolean", "description": "True when facts the assistant cannot know are required."],
                "confidence": ["type": "number", "description": "0 to 1."],
                "meeting_topic": ["type": "string", "description": "Short description of the meeting topic."],
            ])
        case .recap:
            return object([
                "summary": ["type": "string"],
                "key_points": ["type": "array", "items": ["type": "string"]],
                "action_items": ["type": "array", "items": ["type": "string"]],
                "open_questions": ["type": "array", "items": ["type": "string"]],
                "language": ["type": "string"],
                "meeting_topic": ["type": "string"],
            ])
        case .questionsToAsk:
            return object([
                "questions": [
                    "type": "array",
                    "items": object([
                        "question": ["type": "string"],
                        "explanation": ["type": "string", "description": "Explanation in the explanation language, or empty."],
                    ]),
                ],
                "language": ["type": "string"],
                "meeting_topic": ["type": "string"],
            ])
        }
    }

    /// Strict structured-output object: every property required, no extras.
    private static func object(_ properties: KeyValuePairs<String, JSONValue>) -> JSONValue {
        [
            "type": "object",
            "properties": .object(properties.map { JSONField(key: $0.key, value: $0.value) }),
            "required": .array(properties.map { .string($0.key) }),
            "additionalProperties": false,
        ]
    }
}
