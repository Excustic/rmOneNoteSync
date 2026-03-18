#include <stdio.h>
#include <string.h>

static void get_json_value(const char *json, const char *key, char *out,
                           size_t out_size) {
  if (!out || out_size == 0)
    return;
  out[0] = '\0';
  char search_key[128];
  snprintf(search_key, sizeof(search_key), "\"%s\"", key);
  const char *pos = strstr(json, search_key);
  if (pos) {
    const char *colon = strchr(pos, ':');
    if (colon) {
      const char *p = colon + 1;
      while (*p == ' ' || *p == '\t' || *p == '\n' || *p == '\r') p++;
      if (*p == '"') {
        const char *quote2 = strchr(p + 1, '"');
        if (quote2) {
          size_t len = quote2 - (p + 1);
          if (len >= out_size)
            len = out_size - 1;
          strncpy(out, p + 1, len);
          out[len] = '\0';
        }
      } else {
        size_t i = 0;
        while (p[i] && p[i] != ',' && p[i] != '}' && p[i] != ' ' && p[i] != '\n' && p[i] != '\r' && i < out_size - 1) {
          out[i] = p[i];
          i++;
        }
        out[i] = '\0';
      }
    }
  }
}

int main() {
    const char* json = "{\"lastModified\": \"1772969790505\", \"lastOpened\": 1772969778776}";
    char mod[64], op[64];
    get_json_value(json, "lastModified", mod, sizeof(mod));
    get_json_value(json, "lastOpened", op, sizeof(op));
    printf("MOD: '%s'\n", mod);
    printf("OP: '%s'\n", op);
    return 0;
}
