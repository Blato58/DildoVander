#include "BuildDate.h"

#include <cstring>

namespace {

constexpr char MonthNames[][4] = {
    "Jan", "Feb", "Mar", "Apr", "May", "Jun",
    "Jul", "Aug", "Sep", "Oct", "Nov", "Dec"};
constexpr int DaysPerMonth[] = {31, 28, 31, 30, 31, 30,
                                31, 31, 30, 31, 30, 31};

bool IsLeapYear(int year) {
  return year % 4 == 0 && (year % 100 != 0 || year % 400 == 0);
}

int64_t DaysFromCivil(int year, unsigned month, unsigned day) {
  year -= month <= 2;
  const int era = year / 400;
  const unsigned yearOfEra = static_cast<unsigned>(year - era * 400);
  const unsigned adjustedMonth = month > 2 ? month - 3 : month + 9;
  const unsigned dayOfYear =
      (153 * adjustedMonth + 2) / 5 + day - 1;
  const unsigned yearWithinEra =
      yearOfEra * 365 + yearOfEra / 4 - yearOfEra / 100 + dayOfYear;
  return era * 146097 + static_cast<int>(yearWithinEra) - 719468;
}

}  // namespace

uint64_t ParseBuildDateUtc(const char* compilerDate) {
  if (compilerDate == nullptr || std::strlen(compilerDate) != 11) {
    return 0;
  }
  int month = 0;
  for (int index = 0; index < 12; ++index) {
    if (std::strncmp(compilerDate, MonthNames[index], 3) == 0) {
      month = index + 1;
      break;
    }
  }
  if (month == 0 || compilerDate[3] != ' ' ||
      compilerDate[6] != ' ') {
    return 0;
  }
  const char firstDayDigit = compilerDate[4];
  const char secondDayDigit = compilerDate[5];
  if (!((firstDayDigit == ' ') ||
        (firstDayDigit >= '0' && firstDayDigit <= '3')) ||
      secondDayDigit < '0' || secondDayDigit > '9') {
    return 0;
  }
  const int day = (firstDayDigit == ' ' ? 0 : firstDayDigit - '0') * 10 +
                  secondDayDigit - '0';
  int year = 0;
  for (int index = 7; index < 11; ++index) {
    if (compilerDate[index] < '0' || compilerDate[index] > '9') {
      return 0;
    }
    year = year * 10 + compilerDate[index] - '0';
  }
  if (year < 1970 || day < 1 ||
      day > DaysPerMonth[month - 1] +
                (month == 2 && IsLeapYear(year) ? 1 : 0)) {
    return 0;
  }
  return static_cast<uint64_t>(DaysFromCivil(year, month, day)) * 86400u;
}
