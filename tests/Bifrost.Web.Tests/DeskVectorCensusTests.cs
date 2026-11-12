using System.Reflection;
using System.Text.Json.Nodes;
using Bifrost.Web.Tests.Support;

namespace Bifrost.Web.Tests;

// Every section of the vendored vectors file (each top-level key, and each key under an object section down to depth 3) is
// classified here: replayed by a named test of Bifrost's, or not judged by Bifrost because the rule belongs to CultLib's
// validators and Huginn's door. A re-pin that adds a section fails until someone classifies it; a section that goes
// fails too. A replayed section names the test that replays it, and the test must exist.
public sealed class DeskVectorCensusTests
{
    private const string Wire = nameof(DeskVectorWireTests);

    // path -> "Class.Method" of a test that replays it.
    private static readonly Dictionary<string, string> Replayed = new(StringComparer.Ordinal)
    {
        ["card.valid"] = $"{Wire}.{nameof(DeskVectorWireTests.Every_valid_card_leaves_the_door_inside_an_entry_unchanged)}",
        ["request.valid"] = "DeskBodyShapeTests.Valid_request_vectors_pass",
        ["request.invalid"] = "DeskBodyShapeTests.Invalid_request_vectors_refuse_with_their_code",
        ["deskAnswer.valid"] = $"{Wire}.{nameof(DeskVectorWireTests.Every_valid_stored_answer_leaves_an_answered_reply_unchanged_the_withheld_variants_included)}",
        ["results"] = "DeskScenarioTests.Answer_results_pass_through_unchanged",
        ["doorRefusals"] = "DeskDocumentTests.Refusal_and_unavailable_documents_match_vectors",
        ["unavailable"] = "DeskDocumentTests.Refusal_and_unavailable_documents_match_vectors",
        ["scenarios"] = "DeskScenarioTests.Every_scenario_answer_step_replays_through_http",
        ["transport.listRequest.valid"] = "DeskReadTests.Valid_list_queries_reach_the_door_as_typed_requests",
        ["transport.listRequest.invalid"] = "DeskReadTests.List_and_read_request_invalid_vectors",
        ["transport.entry.valid"] = $"{Wire}.{nameof(DeskVectorWireTests.Every_valid_entry_leaves_a_read_unchanged_in_both_views)}",
        ["transport.entry.invalid"] = $"{Wire}.{nameof(DeskVectorWireTests.The_door_refuses_the_entry_negatives_that_break_its_typing_and_judges_no_rule_of_the_contract)}",
        ["transport.listInvalid"] = $"{Wire}.{nameof(DeskVectorWireTests.The_door_refuses_a_negative_count_and_judges_no_other_list_rule)}",
        ["transport.listScenarios"] = $"{Wire}.{nameof(DeskVectorWireTests.Every_list_page_in_the_vectors_leaves_the_door_as_the_vectors_message)}",
        ["transport.listShortPages"] = $"{Wire}.{nameof(DeskVectorWireTests.Every_list_page_in_the_vectors_leaves_the_edge_as_the_vectors_message_for_its_reader)}",
        ["transport.readTooLarge"] = "DeskReadTests.A_read_past_the_cap_follows_the_vectors",
        ["transport.answerTooLarge"] = "DeskAnswerTests.A_stale_answer_over_an_oversize_fork_follows_the_vector_fresh_and_replayed",
        ["transport.doorFaults"] = "DeskReadTests.Door_faults_follow_the_vectors_per_route",
        ["transport.mindRoutes.minds"] = "DeskMindRoutesTests.Every_vector_segment_resolves_or_misses",
        ["transport.mindRoutes.forks"] = "DeskMindRouteTests.Vector_route_cases",
        ["transport.mindRoutes.cases"] = "DeskMindRouteTests.Vector_route_cases",
        ["transport.mindRoutes.sameResponse"] = "DeskMindRouteTests.Same_response_pairs_are_byte_equal",
        ["transport.authCases"] = "DeskAuthCaseTests.Vector_auth_cases",
    };

    // path -> why Bifrost does not judge it.
    private static readonly Dictionary<string, string> NotJudged = new(StringComparer.Ordinal)
    {
        ["provenance"] = "metadata text, not a vector",
        ["optionsHash"] = "Bifrost computes no hash of a card (DeskReaderViewTests pins the 15 and that the Desk source holds no SHA256); CultLib's validators and Huginn's door own the options hash",
        ["bodyDigests"] = "the body digest behind request_id_conflict is the receiver's (Huginn's door); Bifrost forwards the body as written (DeskReaderViewTests pins the 9)",
        ["card.invalid"] = "the card is an opaque map to Bifrost; every card rule (R-card, depends, options, deciders, hash) is CultLib's validators' and Huginn's door's",
        ["deskAnswer.invalid"] = "outcome, code and target pairing are CultLib's and Huginn's; the door judges only the closed 16-key typed shape (all twelve have it; DeskVectorWireTests passes them through)",
        ["resultsInvalid"] = "R-answer-result (the sender's own answer carries their account) is Huginn's door's rule; DeskVectorWireTests reads the three as any answer",
        ["rulingCites"] = "which answer a ruling may cite is a rule of CultLib's validators and the ruling's author; Bifrost forwards answers and rules on nothing",
        ["transport.readRequest.valid"] = "the read request is the route's own path segments (mind, fork id); the golden read.request frame pins what the door sends",
        ["transport.readRequest.invalid"] = "an empty fork id or mind is a routing miss before any request exists (DeskReadTests, DeskMindRoutesTests)",
    };

    private static IEnumerable<string> Sections(JsonNode node, string path, int depth)
    {
        if (node is JsonObject map && depth < 3)
        {
            foreach (var (key, value) in map)
            {
                foreach (var section in Sections(value!, path.Length == 0 ? key : path + "." + key, depth + 1))
                {
                    yield return section;
                }
            }
        }
        else
        {
            yield return path;
        }
    }

    [Fact]
    public void Every_section_of_the_vendored_vectors_is_classified_replayed_or_not_judged_and_no_section_is_both()
    {
        var sections = Sections(DeskVectors.Root, string.Empty, 0).Order(StringComparer.Ordinal).ToList();

        Assert.Empty(Replayed.Keys.Intersect(NotJudged.Keys));
        Assert.Equal(sections, Replayed.Keys.Concat(NotJudged.Keys).Order(StringComparer.Ordinal).ToList());
        Assert.Contains("transport.entry.invalid", sections);
        Assert.Contains("resultsInvalid", sections);
    }

    [Fact]
    public void Every_replayed_section_names_a_test_that_exists()
    {
        var assembly = typeof(DeskVectorCensusTests).Assembly;
        foreach (var (section, test) in Replayed)
        {
            var (className, method) = (test[..test.IndexOf('.', StringComparison.Ordinal)], test[(test.IndexOf('.', StringComparison.Ordinal) + 1)..]);
            var type = assembly.GetType("Bifrost.Web.Tests." + className);
            Assert.True(type is not null, $"{section}: no test class {className}");
            var found = type.GetMethods(BindingFlags.Public | BindingFlags.Instance).FirstOrDefault(m => m.Name == method);
            Assert.True(found is not null && found.GetCustomAttributes<FactAttribute>().Any(), $"{section}: no test {test}");
        }
    }
}
