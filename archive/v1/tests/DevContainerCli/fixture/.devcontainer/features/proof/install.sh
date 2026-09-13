#!/bin/sh
set -eu
printf '#!/bin/sh\nprintf "feature-installed\\n"\n' > /usr/local/bin/hvo-feature-proof
chmod 755 /usr/local/bin/hvo-feature-proof
