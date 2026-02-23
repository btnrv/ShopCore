namespace ShopCore;

internal enum QuizOperator
{
    Add,
    Subtract,
    Multiply,
    Divide
}

internal sealed record QuizQuestion(
    int Id,
    string Expression,
    int Answer,
    int RewardCredits
);

internal sealed class QuizQuestionGenerator
{
    public QuizQuestion CreateQuestion(int id, QuizModuleConfig config, IReadOnlyList<QuizOperator> enabledOperators)
    {
        if (enabledOperators.Count == 0)
        {
            throw new InvalidOperationException("No quiz operators are enabled.");
        }

        var reward = Random.Shared.Next(config.MinimumRewardCredits, config.MaximumRewardCredits + 1);

        for (var attempt = 0; attempt < 64; attempt++)
        {
            var op = enabledOperators[Random.Shared.Next(enabledOperators.Count)];
            if (TryCreateQuestion(op, id, reward, config.MinimumOperand, config.MaximumOperand, out var question))
            {
                return question;
            }
        }

        return CreateAdditionFallback(id, reward, config.MinimumOperand, config.MaximumOperand);
    }

    private static bool TryCreateQuestion(
        QuizOperator op,
        int id,
        int reward,
        int minOperand,
        int maxOperand,
        out QuizQuestion question)
    {
        switch (op)
        {
            case QuizOperator.Add:
            {
                var a = NextInclusive(minOperand, maxOperand);
                var b = NextInclusive(minOperand, maxOperand);
                question = new QuizQuestion(id, $"{a} + {b}", a + b, reward);
                return true;
            }
            case QuizOperator.Subtract:
            {
                var a = NextInclusive(minOperand, maxOperand);
                var b = NextInclusive(minOperand, maxOperand);
                if (a < b)
                {
                    (a, b) = (b, a);
                }

                question = new QuizQuestion(id, $"{a} - {b}", a - b, reward);
                return true;
            }
            case QuizOperator.Multiply:
            {
                var a = NextInclusive(minOperand, maxOperand);
                var b = NextInclusive(minOperand, maxOperand);
                var product = (long)a * b;
                if (product is < int.MinValue or > int.MaxValue)
                {
                    question = default!;
                    return false;
                }

                question = new QuizQuestion(id, $"{a} x {b}", (int)product, reward);
                return true;
            }
            case QuizOperator.Divide:
            {
                var positiveMin = Math.Max(1, minOperand);
                var positiveMax = Math.Max(positiveMin, maxOperand);
                var foundAny = false;
                var foundNonTrivial = false;
                var chosenDivisor = 1;
                var chosenQuotient = 1;

                // Prefer division questions that are not "/ 1" and do not evaluate to 1.
                for (var i = 0; i < 24; i++)
                {
                    var divisor = NextInclusive(positiveMin, positiveMax);
                    var minQuotient = (int)Math.Ceiling(minOperand / (double)divisor);
                    var maxQuotient = (int)Math.Floor(maxOperand / (double)divisor);

                    if (maxQuotient < minQuotient)
                    {
                        continue;
                    }

                    foundAny = true;

                    var preferredMinQuotient = Math.Max(minQuotient, 2);
                    var canAvoidOneResult = maxQuotient >= preferredMinQuotient;
                    var canAvoidOneDivisor = divisor > 1;

                    if (!canAvoidOneResult || !canAvoidOneDivisor)
                    {
                        if (!foundNonTrivial)
                        {
                            chosenDivisor = divisor;
                            chosenQuotient = NextInclusive(minQuotient, maxQuotient);
                        }

                        continue;
                    }

                    chosenDivisor = divisor;
                    chosenQuotient = NextInclusive(preferredMinQuotient, maxQuotient);
                    foundNonTrivial = true;
                    break;
                }

                if (!foundAny)
                {
                    question = default!;
                    return false;
                }

                var dividend = chosenDivisor * chosenQuotient;
                question = new QuizQuestion(id, $"{dividend} / {chosenDivisor}", chosenQuotient, reward);
                return true;
            }
            default:
                question = default!;
                return false;
        }
    }

    private static QuizQuestion CreateAdditionFallback(int id, int reward, int minOperand, int maxOperand)
    {
        var a = NextInclusive(minOperand, maxOperand);
        var b = NextInclusive(minOperand, maxOperand);
        return new QuizQuestion(id, $"{a} + {b}", a + b, reward);
    }

    private static int NextInclusive(int minValue, int maxValue)
    {
        if (maxValue < minValue)
        {
            (minValue, maxValue) = (maxValue, minValue);
        }

        return Random.Shared.Next(minValue, maxValue + 1);
    }
}
