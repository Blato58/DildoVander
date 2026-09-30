#include "TurnPollGuard.h"

namespace {

bool ParseUtcExpiry(const std::string& value, uint64_t& seconds) {
  // DateTimeOffset from the backend uses UTC with optional fractional seconds.
  if (value.size() < 20 || value.size() > 35 || value[4] != '-' ||
      value[7] != '-' || value[10] != 'T' || value[13] != ':' ||
      value[16] != ':') {
    return false;
  }
  const int positions[] = {0, 1, 2, 3, 5, 6, 8, 9, 11, 12, 14, 15, 17, 18};
  for (int position : positions) {
    if (value[position] < '0' || value[position] > '9') {
      return false;
    }
  }
  const auto number = [&value](size_t offset, size_t count) {
    int result = 0;
    for (size_t index = 0; index < count; ++index) {
      result = result * 10 + value[offset + index] - '0';
    }
    return result;
  };
  const int year = number(0, 4);
  const int month = number(5, 2);
  const int day = number(8, 2);
  const int hour = number(11, 2);
  const int minute = number(14, 2);
  const int second = number(17, 2);
  if (year < 1970 || month < 1 || month > 12 || hour > 23 ||
      minute > 59 || second > 59) {
    return false;
  }
  const int monthDays[] = {31, 28, 31, 30, 31, 30, 31, 31, 30, 31, 30, 31};
  const bool leap = year % 4 == 0 && (year % 100 != 0 || year % 400 == 0);
  if (day < 1 || day > monthDays[month - 1] + (month == 2 && leap ? 1 : 0)) {
    return false;
  }
  size_t suffix = 19;
  if (value[suffix] == '.') {
    ++suffix;
    const size_t firstDigit = suffix;
    while (suffix < value.size() && value[suffix] >= '0' &&
           value[suffix] <= '9' && suffix - firstDigit < 7) {
      ++suffix;
    }
    if (suffix == firstDigit) {
      return false;
    }
  }
  if (!(value.substr(suffix) == "Z" ||
        value.substr(suffix) == "+00:00")) {
    return false;
  }

  // Gregorian civil date to days since 1970-01-01.
  int adjustedYear = year - (month <= 2 ? 1 : 0);
  const int era = adjustedYear / 400;
  const unsigned yearOfEra = static_cast<unsigned>(adjustedYear - era * 400);
  const unsigned shiftedMonth = static_cast<unsigned>(month + (month > 2 ? -3 : 9));
  const unsigned dayOfYear = (153 * shiftedMonth + 2) / 5 + day - 1;
  const unsigned yearOfEraDay = yearOfEra * 365 + yearOfEra / 4 -
                                yearOfEra / 100 + dayOfYear;
  const int64_t days = era * 146097 + static_cast<int64_t>(yearOfEraDay) - 719468;
  if (days < 0) {
    return false;
  }
  seconds = static_cast<uint64_t>(days) * 86400 + hour * 3600 +
            minute * 60 + second;
  return true;
}

}  // namespace

TurnPollDecision EvaluateTurnOperation(
    const std::string& expectedOperationId,
    const TurnCorrelation& expected,
    uint64_t currentGeneration,
    uint64_t currentDeviceRevision,
    const TurnOperationObservation& observed) {
  const TurnCorrelation& actual = observed.correlation;
  if (expectedOperationId.empty() ||
      observed.operationId != expectedOperationId ||
      observed.kind != "turn" ||
      actual.deviceId != expected.deviceId ||
      actual.bootId != expected.bootId ||
      actual.conversationId != expected.conversationId ||
      actual.clientTurnId != expected.clientTurnId ||
      actual.generation != expected.generation ||
      actual.deviceRevision != expected.deviceRevision ||
      currentGeneration != expected.generation ||
      currentDeviceRevision != expected.deviceRevision) {
    return TurnPollDecision::Discard;
  }

  switch (observed.status) {
    case TurnOperationStatus::Pending:
    case TurnOperationStatus::Running:
      return TurnPollDecision::Wait;
    case TurnOperationStatus::Failed:
    case TurnOperationStatus::Cancelled:
    case TurnOperationStatus::Unknown:
      return TurnPollDecision::Terminal;
    case TurnOperationStatus::Completed:
      if (!observed.hasProposal &&
          (observed.decision == "ask" ||
           observed.decision == "no_selection") &&
          !observed.reply.empty() && observed.reply.size() <= 2000) {
        return TurnPollDecision::ReplyReady;
      }
      return TurnPollDecision::Discard;
  }
  return TurnPollDecision::Discard;
}

TurnPollDecision EvaluateTurnOperationWithProposal(
    const std::string& expectedOperationId,
    const TurnCorrelation& expected,
    uint64_t currentGeneration,
    uint64_t currentDeviceRevision,
    const std::string& currentCatalogVersion,
    uint8_t currentReadyMask,
    uint64_t nowUtcSeconds,
    const TurnOperationObservation& observed) {
  const TurnPollDecision basic = EvaluateTurnOperation(
      expectedOperationId, expected, currentGeneration,
      currentDeviceRevision, observed);
  if (basic != TurnPollDecision::Discard ||
      observed.status != TurnOperationStatus::Completed ||
      observed.decision != "propose" || !observed.hasProposal ||
      observed.reply.empty() || observed.reply.size() > 2000) {
    return basic;
  }
  const TurnCorrelation& actual = observed.correlation;
  const TurnProposal& proposal = observed.proposal;
  uint64_t expiry = 0;
  if (expectedOperationId.empty() || observed.operationId != expectedOperationId ||
      observed.kind != "turn" || actual.deviceId != expected.deviceId ||
      actual.bootId != expected.bootId ||
      actual.conversationId != expected.conversationId ||
      actual.clientTurnId != expected.clientTurnId ||
      actual.generation != expected.generation ||
      actual.deviceRevision != expected.deviceRevision ||
      currentGeneration != expected.generation ||
      currentDeviceRevision != expected.deviceRevision ||
      proposal.conversationId != expected.conversationId ||
      proposal.turnId != expected.clientTurnId ||
      proposal.bootId != expected.bootId ||
      proposal.generation != expected.generation ||
      proposal.deviceRevision != expected.deviceRevision ||
      currentCatalogVersion.empty() ||
      proposal.catalogVersion != currentCatalogVersion ||
      proposal.compartment < 1 || proposal.compartment > 6 ||
      (currentReadyMask & (1u << (proposal.compartment - 1))) == 0 ||
      !ParseUtcExpiry(proposal.expiresAt, expiry) ||
      expiry <= nowUtcSeconds || expiry - nowUtcSeconds <= 1 ||
      expiry - nowUtcSeconds > 60) {
    return TurnPollDecision::Discard;
  }
  return TurnPollDecision::ProposalReady;
}

uint64_t ProposalRemainingMs(const TurnProposal& proposal,
                             uint64_t nowUtcSeconds) {
  uint64_t expiry = 0;
  if (!ParseUtcExpiry(proposal.expiresAt, expiry) ||
      expiry <= nowUtcSeconds || expiry - nowUtcSeconds <= 1 ||
      expiry - nowUtcSeconds > 60) {
    return 0;
  }
  return (expiry - nowUtcSeconds - 1) * 1000;
}
